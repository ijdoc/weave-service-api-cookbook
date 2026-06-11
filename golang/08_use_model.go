// Recipe 08: create a versioned Model + use it in a trace.
//
// First application of ADR-0004 (the source-embedding scaffold). The
// recipe creates two Weave Objects:
//
//   POST /v2/{entity}/{project}/ops   -> register the predict Op
//                                        (Python scaffold per ADR-0004)
//   POST /obj/create                  -> register the Model object,
//                                        pointing val.predict at the
//                                        predict Op's weave:// ref
//
// Then it opens a Call that references both — establishing the "predict
// logic lives in the recipe file; Weave records identity + invocation"
// pattern that recipes 09-12 reuse.
//
// Wire-level points worth knowing:
//
//   - The Model is created via /obj/create, NOT /v2/.../models. The
//     generic Object endpoint takes structured metadata (a predict field
//     pointing at the Op ref) that makes the UI render predict inline.
//   - The Model val mirrors the SDK shape: _bases=["Model","Object",
//     "BaseModel"], _class_name/_type a real subclass name, a predict
//     weave:// ref, plus instance attributes (model_name, temperature,
//     max_tokens) that distinguish one Model version from another.
//     Per-Call data (the question, the answer) lives on the Call.
//   - The UI's CallPage parses op_name and inputs.self as weave:// URIs
//     and crashes on raw strings — both MUST be real refs.
//
// Editing this file changes its SHA256 -> the Op scaffold changes ->
// Weave bumps the predict Op's version_index. Per-language identity comes
// from the Model + Op object_ids (recipe-08-model-golang[.predict]).
//
// For brevity this recipe mocks the LLM invocation — the Call's output is
// a hardcoded answer.
//
// Run:
//   go run golang/08_use_model.go

package main

import (
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"strings"
	"time"
)

const recipePath = "golang/08_use_model.go"

var (
	entity    = os.Getenv("WANDB_ENTITY")
	project   = os.Getenv("WANDB_PROJECT")
	projectID = entity + "/" + project
	baseURL   = getenv("WEAVE_SERVICE_URL", "https://trace.wandb.ai")
	apiKey    = os.Getenv("WANDB_API_KEY")
	client    = &http.Client{}
)

func getenv(key, def string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return def
}

func fatal(format string, args ...any) {
	fmt.Fprintf(os.Stderr, format+"\n", args...)
	os.Exit(1)
}

// post centralizes auth + JSON (de)serialization. Any non-2xx is fatal.
func post(path string, body map[string]any) map[string]any {
	payload, err := json.Marshal(body)
	if err != nil {
		fatal("encode %s: %v", path, err)
	}
	req, err := http.NewRequest(http.MethodPost, baseURL+path, bytes.NewReader(payload))
	if err != nil {
		fatal("request %s: %v", path, err)
	}
	req.SetBasicAuth("api", apiKey)
	req.Header.Set("Content-Type", "application/json")
	res, err := client.Do(req)
	if err != nil {
		fatal("post %s: %v", path, err)
	}
	defer res.Body.Close()
	raw, _ := io.ReadAll(res.Body)
	if res.StatusCode/100 != 2 {
		fatal("HTTP %d for %s: %s", res.StatusCode, path, raw)
	}
	if len(raw) == 0 {
		return map[string]any{}
	}
	var out map[string]any
	if err := json.Unmarshal(raw, &out); err != nil {
		fatal("decode %s: %v", path, err)
	}
	return out
}

func main() {
	var missing []string
	for _, k := range []string{"WANDB_API_KEY", "WANDB_ENTITY", "WANDB_PROJECT"} {
		if os.Getenv(k) == "" {
			missing = append(missing, k)
		}
	}
	if len(missing) > 0 {
		fatal("Missing required env vars: %s. See ../README.md#setup.", strings.Join(missing, ", "))
	}

	// --- ADR-0004 scaffold for the predict Op ---
	// SHA256 of this recipe file's bytes (read relative to the repo root,
	// the documented run CWD). Edits flow through to opSource, which is what
	// Weave content-addresses on; re-running an unchanged file is idempotent.
	recipeBytes, err := os.ReadFile(recipePath)
	if err != nil {
		fatal("read recipe source %s: %v", recipePath, err)
	}
	sum := sha256.Sum256(recipeBytes)
	recipeSha := hex.EncodeToString(sum[:])[:16]

	opSource := fmt.Sprintf(`# Cookbook scaffold (golang)
# Source: %s
# SHA256: %s

import weave


@weave.op
def predict(self, question):
    """The actual predict implementation lives in:
        %s

    Byte-for-byte reference (SHA256 of the recipe file):
        %s

    To verify a local copy of the file matches (POSIX shell):
        shasum -a 256 %s | cut -c1-16

    This Python op is a metadata handle, not the real model — running
    it raises NotImplementedError by design.
    """
    raise NotImplementedError(
        "This op is a Python scaffold uploaded from a non-Python recipe. "
        "See the docstring above for the real source-language file and a "
        "verifiable byte-for-byte reference (SHA256)."
    )
`, recipePath, recipeSha, recipePath, recipeSha, recipePath)

	// 1) Register the predict Op via the specialized /v2/.../ops endpoint.
	// Object_id is <ClassName>.predict by convention; the server lowercases
	// it. The Op carries the ADR-0004 scaffold as its source.
	opName := "recipe-08-model-golang.predict"
	opRes := post(fmt.Sprintf("/v2/%s/%s/ops", entity, project), map[string]any{
		"name":        opName,
		"source_code": opSource,
	})
	predictOpRef := fmt.Sprintf("weave:///%s/op/%s:%s", projectID, opRes["object_id"], opRes["digest"])
	fmt.Printf("Predict op: %v digest=%v… version=%v\n", opRes["object_id"], opRes["digest"].(string)[:12], opRes["version_index"])

	// 2) Register the Model via the generic /obj/create endpoint. The val
	// mirrors the SDK's Model shape; instance attributes are the kind of
	// config a real Model carries — change any value and you get a new
	// (digest, version_index).
	modelObjectID := "recipe-08-model-golang"
	modelVal := map[string]any{
		"_bases":      []string{"Model", "Object", "BaseModel"},
		"_class_name": "Recipe08GolangModel",
		"_type":       "Recipe08GolangModel",
		"name":        modelObjectID,
		"description": "Cookbook model handle (golang recipe 08)",
		"model_name":  "gpt-4o-mini",
		"temperature": 0.7,
		"max_tokens":  100,
		"predict":     predictOpRef,
	}
	modelRes := post("/obj/create", map[string]any{
		"obj": map[string]any{
			"project_id": projectID,
			"object_id":  modelObjectID,
			"val":        modelVal,
		},
	})
	modelDigest := modelRes["digest"].(string)
	modelRef := fmt.Sprintf("weave:///%s/object/%s:%s", projectID, modelObjectID, modelDigest)
	fmt.Printf("Model:      %v digest=%s…\n", modelRes["object_id"], modelDigest[:12])
	fmt.Printf("  ref: %s\n", modelRef)

	// 3) Open a Call that uses the predict Op + Model. op_name MUST be the Op
	// ref (not a bare string), and inputs.self MUST be the Model ref.
	question := "Is the sky blue?"
	started := post("/call/start", map[string]any{
		"start": map[string]any{
			"project_id": projectID,
			"op_name":    predictOpRef,
			"started_at": time.Now().UTC().Format(time.RFC3339Nano),
			"attributes": map[string]any{
				"cookbook.language":    "golang",
				"cookbook.recipe":      "08_use_model",
				"cookbook.environment": getenv("COOKBOOK_ENVIRONMENT", "dev"),
			},
			"inputs": map[string]any{"self": modelRef, "question": question},
		},
	})
	callID := started["id"].(string)
	traceID := started["trace_id"].(string)
	fmt.Printf("Started:    id=%s\n", callID)

	// 4) End the Call with the model's answer. A real recipe would call the
	// LLM named in model_name here; we hardcode an answer to stay focused on
	// the wire-level Model + Op + Call wiring.
	answer := "yes"
	post("/call/end", map[string]any{
		"end": map[string]any{
			"project_id": projectID,
			"id":         callID,
			"ended_at":   time.Now().UTC().Format(time.RFC3339Nano),
			"summary": map[string]any{
				"status_counts": map[string]any{"success": 1, "error": 0},
				"weave":         map[string]any{"status": "success", "trace_name": opName},
			},
			"output": answer,
		},
	})
	fmt.Printf("Ended:      id=%s output=%q\n", callID, answer)

	// --- verification ---
	// Read the Call back and assert the model + op linkage round-trips.
	var call map[string]any
	for i := 0; i < 5; i++ {
		res := post("/call/read", map[string]any{"project_id": projectID, "id": callID})
		if c, ok := res["call"].(map[string]any); ok && c["ended_at"] != nil {
			call = c
			break
		}
		time.Sleep(time.Second)
	}
	if call == nil || call["ended_at"] == nil {
		fatal("FAIL: Call %s not visible/finished after 5 reads", callID)
	}

	if call["op_name"] != predictOpRef {
		fatal("op_name: %v", call["op_name"])
	}
	ins, _ := call["inputs"].(map[string]any)
	if ins["self"] != modelRef {
		fatal("inputs.self: %v", ins["self"])
	}
	if ins["question"] != question {
		fatal("inputs.question: %v", ins["question"])
	}
	if call["output"] != answer {
		fatal("output: %v", call["output"])
	}
	if call["trace_id"] != traceID {
		fatal("trace_id: %v", call["trace_id"])
	}
	fmt.Printf("Verified:   id=%s (op + model + output round-tripped)\n", callID)
}
