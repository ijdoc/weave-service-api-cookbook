// Recipe 07: publish a Prompt + reference it from a Call + tag/alias it.
//
// Introduces four new things that recipes 08-13 build on:
//
//   POST /obj/create                       -> generic Weave Object endpoint;
//                                             here, publish a StringPrompt
//   POST /obj/read                         -> read it back
//   PUT  /objs/{id}/versions/{digest}/tags -> add version tags
//   PUT  /objs/{id}/aliases                -> set named pointers
//
// (and the existing /call/start + /call/end, but now with inputs.prompt =
// a weave:// ref to the Prompt — the "object ref in trace inputs" pattern
// that unlocks Model.predict, Scorer Ops, and the eval flow.)
//
// Wire-level points worth knowing:
//
//   - The Object endpoint is flat under an `obj` wrapper:
//     {"obj": {"project_id", "object_id", "val"}}. The val is stored
//     verbatim (after lowercasing object_id) and MUST carry _bases,
//     _class_name, and _type for the UI to recognise the object.
//   - base_object_class ("Prompt") is derived from val._bases;
//     leaf_object_class from val._class_name.
//   - Tags are per-version additive labels; aliases are per-object_id
//     named pointers. Both are UI-visible metadata separate from val, so
//     changing them does NOT bump the version. The server auto-maintains
//     a `latest` alias — do not set it yourself.
//
// Run:
//   go run golang/07_use_prompt.go

package main

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"strings"
	"time"
)

var (
	baseURL   = getenv("WEAVE_SERVICE_URL", "https://trace.wandb.ai")
	projectID = os.Getenv("WANDB_ENTITY") + "/" + os.Getenv("WANDB_PROJECT")
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

// doJSON centralizes auth + JSON (de)serialization for any method. Any
// non-2xx is fatal. post/put wrap it for the two verbs this recipe uses.
func doJSON(method, path string, body map[string]any) map[string]any {
	payload, err := json.Marshal(body)
	if err != nil {
		fatal("encode %s: %v", path, err)
	}
	req, err := http.NewRequest(method, baseURL+path, bytes.NewReader(payload))
	if err != nil {
		fatal("request %s: %v", path, err)
	}
	req.SetBasicAuth("api", apiKey)
	req.Header.Set("Content-Type", "application/json")
	res, err := client.Do(req)
	if err != nil {
		fatal("%s %s: %v", method, path, err)
	}
	defer res.Body.Close()
	raw, _ := io.ReadAll(res.Body)
	if res.StatusCode/100 != 2 {
		fatal("HTTP %d for %s %s: %s", res.StatusCode, method, path, raw)
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

func post(path string, body map[string]any) map[string]any {
	return doJSON(http.MethodPost, path, body)
}
func put(path string, body map[string]any) map[string]any { return doJSON(http.MethodPut, path, body) }

// containsStr reports whether a JSON array (decoded as []any) holds s.
func containsStr(arr any, s string) bool {
	items, ok := arr.([]any)
	if !ok {
		return false
	}
	for _, it := range items {
		if it == s {
			return true
		}
	}
	return false
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

	// 1) Publish a StringPrompt via the generic Object endpoint. The val
	// mirrors what the SDK produces for weave.StringPrompt(content=...).
	promptObjectID := "recipe-07-prompt-golang"
	promptContent := "Answer the question concisely: {question}"
	promptVal := map[string]any{
		"_bases":      []string{"Prompt", "Object", "BaseModel"},
		"_class_name": "StringPrompt",
		"_type":       "StringPrompt",
		"name":        nil,
		"description": "Capital-city Q&A prompt template (golang recipe 07)",
		"content":     promptContent,
	}
	created := post("/obj/create", map[string]any{
		"obj": map[string]any{
			"project_id": projectID,
			"object_id":  promptObjectID,
			"val":        promptVal,
		},
	})
	promptDigest := created["digest"].(string)
	promptRef := fmt.Sprintf("weave:///%s/object/%s:%s", projectID, promptObjectID, promptDigest)
	fmt.Printf("Published: %s digest=%s…\n", promptObjectID, promptDigest[:12])
	fmt.Printf("  ref: %s\n", promptRef)

	// 2) Tag this version with the cookbook environment + language. Tags are
	// per-version, additive, UI-visible labels separate from val. PUT is
	// additive (re-runs are no-ops); removal uses POST .../tags/remove.
	tagsToAdd := []string{getenv("COOKBOOK_ENVIRONMENT", "dev"), "golang"}
	put(fmt.Sprintf("/objs/%s/versions/%s/tags", promptObjectID, promptDigest), map[string]any{
		"project_id": projectID,
		"tags":       tagsToAdd,
	})
	fmt.Printf("Tagged:    %v -> version %s…\n", tagsToAdd, promptDigest[:12])

	// 3) Set named aliases pointing at this version. Aliases are per-object_id
	// named pointers — re-PUTting later on another version detaches them.
	aliasesToSet := []string{"staging", "v1-candidate"}
	put(fmt.Sprintf("/objs/%s/aliases", promptObjectID), map[string]any{
		"project_id": projectID,
		"digest":     promptDigest,
		"aliases":    aliasesToSet,
	})
	fmt.Printf("Aliased:   %v -> version %s…\n", aliasesToSet, promptDigest[:12])

	// 4) Read it back (with tags + aliases) and assert everything round-trips.
	readBack := post("/obj/read", map[string]any{
		"project_id":               projectID,
		"object_id":                promptObjectID,
		"digest":                   promptDigest,
		"include_tags_and_aliases": true,
	})
	obj := readBack["obj"].(map[string]any)
	val := obj["val"].(map[string]any)
	if val["_class_name"] != "StringPrompt" {
		fatal("_class_name: %v", val["_class_name"])
	}
	if val["content"] != promptContent {
		fatal("content: %v", val["content"])
	}
	if obj["base_object_class"] != "Prompt" {
		fatal("base_object_class: %v", obj["base_object_class"])
	}
	if obj["leaf_object_class"] != "StringPrompt" {
		fatal("leaf_object_class: %v", obj["leaf_object_class"])
	}
	for _, t := range tagsToAdd {
		if !containsStr(obj["tags"], t) {
			fatal("tag %q missing from %v", t, obj["tags"])
		}
	}
	for _, a := range aliasesToSet {
		if !containsStr(obj["aliases"], a) {
			fatal("alias %q missing from %v", a, obj["aliases"])
		}
	}
	fmt.Printf("Read:      version=%v tags=%v aliases=%v\n", obj["version_index"], obj["tags"], obj["aliases"])

	// 5) Open a Call whose inputs.prompt is the Prompt's weave:// ref — the
	// "object ref in trace inputs" pattern. The UI follows this ref and
	// renders the prompt content inline in the call view.
	question := "What is the capital of France?"
	started := post("/call/start", map[string]any{
		"start": map[string]any{
			"project_id": projectID,
			"op_name":    "recipe-07-prompt-in-trace",
			"started_at": time.Now().UTC().Format(time.RFC3339Nano),
			"attributes": map[string]any{
				"cookbook.language":    "golang",
				"cookbook.recipe":      "07_use_prompt",
				"cookbook.environment": getenv("COOKBOOK_ENVIRONMENT", "dev"),
			},
			"inputs": map[string]any{"prompt": promptRef, "question": question},
		},
	})
	callID := started["id"].(string)
	traceID := started["trace_id"].(string)
	fmt.Printf("Started:   id=%s (inputs.prompt = %s)\n", callID, promptRef)

	// Client-side: substitute the question into the prompt template.
	rendered := strings.ReplaceAll(promptContent, "{question}", question)
	answer := "Paris"

	post("/call/end", map[string]any{
		"end": map[string]any{
			"project_id": projectID,
			"id":         callID,
			"ended_at":   time.Now().UTC().Format(time.RFC3339Nano),
			"summary":    map[string]any{},
			"output":     map[string]any{"rendered_prompt": rendered, "answer": answer},
		},
	})
	fmt.Printf("Ended:     id=%s output.answer=%q\n", callID, answer)

	// --- verification ---
	// Read the Call back and assert inputs.prompt round-trips as the same
	// weave:// URI we sent. Brief retry tolerates read-after-write lag.
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

	ins, _ := call["inputs"].(map[string]any)
	if ins["prompt"] != promptRef {
		fatal("inputs.prompt: %v", ins["prompt"])
	}
	if ins["question"] != question {
		fatal("inputs.question: %v", ins["question"])
	}
	out, _ := call["output"].(map[string]any)
	if out["answer"] != answer {
		fatal("output.answer: %v", out["answer"])
	}
	if call["trace_id"] != traceID {
		fatal("trace_id: %v", call["trace_id"])
	}
	fmt.Println("Verified:  prompt ref round-trips in call inputs")
}
