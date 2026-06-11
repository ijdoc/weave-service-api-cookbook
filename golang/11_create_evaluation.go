// Recipe 11: set up an Evaluation Object.
//
// Pulls everything from earlier recipes together into a single Evaluation
// definition — the versioned, content-addressed Object that recipe 12
// executes and recipe 13 queries against. After this runs, the W&B UI's
// Evaluation Definitions page shows it as a definition with no runs yet.
//
// The recipe builds two kinds of artifacts:
//
//  1. Three canonical Eval Ops (Evaluation.evaluate,
//     Evaluation.predict_and_score, Evaluation.summarize) — inert
//     lifecycle-marker Ops registered via the two-step /file/create +
//     /obj/create flow with ADR-0004 scaffolds. The service identifies
//     these by object_id (case-sensitive — /eval_results/query filters
//     on the exact canonical names), so the object_ids stay SHARED
//     across languages (no -golang suffix), unlike Model/Scorer/Dataset.
//  2. The Evaluation Object itself — POST /obj/create with
//     builtin_object_class="Evaluation", referencing the canonical Ops +
//     the recipe-08 Model + recipe-09 Scorer Op + recipe-10 Dataset.
//
// The canonical Op scaffolds live ONLY here (not in recipes 12/13) so
// editing the eval's definition is a single-file change. /file/create is
// the ONE multipart endpoint the cookbook uses; everything else is JSON.
//
// Run:
//   go run golang/11_create_evaluation.go

package main

import (
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"mime/multipart"
	"net/http"
	"os"
	"strings"
	"time"
)

const recipePath = "golang/11_create_evaluation.go"

var (
	entity    = os.Getenv("WANDB_ENTITY")
	project   = os.Getenv("WANDB_PROJECT")
	projectID = entity + "/" + project
	baseURL   = getenv("WEAVE_SERVICE_URL", "https://trace.wandb.ai")
	apiKey    = os.Getenv("WANDB_API_KEY")
	client    = &http.Client{}
	recipeSha string
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

// do issues an auth'd request (optional JSON body) and decodes the JSON
// response. Any non-2xx is fatal. post/put wrap it.
func do(method, path string, body map[string]any) map[string]any {
	var reader io.Reader
	if body != nil {
		payload, err := json.Marshal(body)
		if err != nil {
			fatal("encode %s: %v", path, err)
		}
		reader = bytes.NewReader(payload)
	}
	req, err := http.NewRequest(method, baseURL+path, reader)
	if err != nil {
		fatal("request %s: %v", path, err)
	}
	req.SetBasicAuth("api", apiKey)
	if body != nil {
		req.Header.Set("Content-Type", "application/json")
	}
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

func post(path string, body map[string]any) map[string]any { return do(http.MethodPost, path, body) }
func put(path string, body map[string]any) map[string]any  { return do(http.MethodPut, path, body) }

// uploadOpSource uploads Op source as a multipart file and returns the file
// digest, which goes into the Op's val under files.obj.py. /file/create is
// the only multipart endpoint the cookbook uses.
func uploadOpSource(source string) string {
	var buf bytes.Buffer
	w := multipart.NewWriter(&buf)
	if err := w.WriteField("project_id", projectID); err != nil {
		fatal("multipart field: %v", err)
	}
	part, err := w.CreateFormFile("file", "obj.py")
	if err != nil {
		fatal("multipart file: %v", err)
	}
	if _, err := part.Write([]byte(source)); err != nil {
		fatal("multipart write: %v", err)
	}
	w.Close()
	req, err := http.NewRequest(http.MethodPost, baseURL+"/file/create", &buf)
	if err != nil {
		fatal("request /file/create: %v", err)
	}
	req.SetBasicAuth("api", apiKey)
	req.Header.Set("Content-Type", w.FormDataContentType())
	res, err := client.Do(req)
	if err != nil {
		fatal("post /file/create: %v", err)
	}
	defer res.Body.Close()
	raw, _ := io.ReadAll(res.Body)
	if res.StatusCode/100 != 2 {
		fatal("HTTP %d for /file/create: %s", res.StatusCode, raw)
	}
	var out map[string]any
	if err := json.Unmarshal(raw, &out); err != nil {
		fatal("decode /file/create: %v", err)
	}
	return out["digest"].(string)
}

// latestObject returns the latest version of object_id, or nil if absent.
func latestObject(objectID string) map[string]any {
	r := post("/objs/query", map[string]any{
		"project_id":    projectID,
		"filter":        map[string]any{"object_ids": []string{objectID}, "latest_only": true},
		"metadata_only": true,
	})
	objs, _ := r["objs"].([]any)
	if len(objs) == 0 {
		return nil
	}
	o, _ := objs[0].(map[string]any)
	return o
}

// latestDatasetByPrefix finds the most-recently-created Dataset whose
// object_id starts with prefix (recipe 10 timestamps Dataset names, so an
// exact object_id lookup won't work).
func latestDatasetByPrefix(prefix string) map[string]any {
	r := post("/objs/query", map[string]any{
		"project_id":    projectID,
		"filter":        map[string]any{"base_object_classes": []string{"Dataset"}},
		"sort_by":       []map[string]any{{"field": "created_at", "direction": "desc"}},
		"limit":         50,
		"metadata_only": true,
	})
	objs, _ := r["objs"].([]any)
	for _, it := range objs {
		o, _ := it.(map[string]any)
		if id, _ := o["object_id"].(string); strings.HasPrefix(id, prefix) {
			return o
		}
	}
	return nil
}

func objRef(o map[string]any) string {
	return fmt.Sprintf("weave:///%s/object/%s:%s", projectID, o["object_id"], o["digest"])
}

func opRef(o map[string]any) string {
	return fmt.Sprintf("weave:///%s/op/%s:%s", projectID, o["object_id"], o["digest"])
}

// scaffold returns an ADR-0004 Python scaffold for a canonical Eval Op. The
// body is inert; the service identifies the Op by object_id, not behaviour.
func scaffold(opName, signature, bodyDoc string) string {
	return fmt.Sprintf(`# Cookbook scaffold (golang)
# Source: %s
# SHA256: %s

import weave


@weave.op
def %s:
    """%s

    Byte-for-byte reference (SHA256 of the recipe file):
        %s

    To verify a local copy of the file matches (POSIX shell):
        shasum -a 256 %s | cut -c1-16

    Canonical lifecycle-marker Op for the cookbook's eval flow. The
    W&B service identifies this Op by `+"`object_id`"+` (%q) and uses it
    to recognise the structured Call trace recipe 12 builds. The body
    raises NotImplementedError by design — real eval logic lives
    client-side in recipe 12.
    """
    raise NotImplementedError(
        "This op is a Python scaffold uploaded from a non-Python recipe. "
        "See the docstring above for the real source-language file and a "
        "verifiable byte-for-byte reference (SHA256)."
    )
`, recipePath, recipeSha, signature, bodyDoc, recipeSha, recipePath, opName)
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

	recipeBytes, err := os.ReadFile(recipePath)
	if err != nil {
		fatal("read recipe source %s: %v", recipePath, err)
	}
	sum := sha256.Sum256(recipeBytes)
	recipeSha = hex.EncodeToString(sum[:])[:16]

	// 1) Look up the prerequisites from earlier recipes.
	model := latestObject("recipe-08-model-golang")
	if model == nil {
		fatal("FAIL: model `recipe-08-model-golang` not found. Run golang/08_use_model.go first.")
	}
	fmt.Printf("Found:     model    %v digest=%v…\n", model["object_id"], model["digest"].(string)[:12])

	scorer := latestObject("recipe-09-is-correct-golang")
	if scorer == nil {
		fatal("FAIL: scorer `recipe-09-is-correct-golang` not found. Run golang/09_score_a_call.go first.")
	}
	fmt.Printf("Found:     scorer   %v digest=%v…\n", scorer["object_id"], scorer["digest"].(string)[:12])

	dataset := latestDatasetByPrefix("recipe-10-dataset-golang")
	if dataset == nil {
		fatal("FAIL: no Dataset matching `recipe-10-dataset-golang-*` found. Run golang/10_create_dataset.go first.")
	}
	fmt.Printf("Found:     dataset  %v digest=%v…\n", dataset["object_id"], dataset["digest"].(string)[:12])

	// 2) Register the three canonical Eval Ops with ADR-0004 scaffolds.
	type canonOp struct{ id, signature, bodyDoc string }
	canonicalOps := []canonOp{
		{"Evaluation.evaluate", "evaluate(self, model)",
			"Root of an evaluation Call trace. Wraps one full pass over\n        the dataset with the given model + scorers."},
		{"Evaluation.predict_and_score", "predict_and_score(self, example)",
			"Per-row child of the eval root. One trial = one dataset row\n        scored by all configured scorers."},
		{"Evaluation.summarize", "summarize(self, eval_table)",
			"Final sibling of predict_and_score children under the root.\n        Aggregates per-row scorer outputs into evaluation-level stats."},
	}
	evalOpRefs := map[string]string{}
	for _, op := range canonicalOps {
		fileDigest := uploadOpSource(scaffold(op.id, op.signature, op.bodyDoc))
		res := post("/obj/create", map[string]any{
			"obj": map[string]any{
				"project_id": projectID,
				"object_id":  op.id,
				"val": map[string]any{
					"_type":      "CustomWeaveType",
					"files":      map[string]any{"obj.py": fileDigest},
					"weave_type": map[string]any{"type": "Op"},
				},
			},
		})
		evalOpRefs[op.id] = fmt.Sprintf("weave:///%s/op/%s:%s", projectID, res["object_id"], res["digest"])
		fmt.Printf("Op:        %v digest=%v… (file=%v…)\n", res["object_id"], res["digest"].(string)[:12], fileDigest[:12])
	}

	// 3) Build the Evaluation Object. The val mirrors the SDK shape: each
	// canonical Op is a structured method field, scorers is a list of refs.
	evalObjectID := "recipe-11-eval-golang"
	evalVal := map[string]any{
		"_bases":                 []string{"Object", "BaseModel"},
		"_class_name":            "Evaluation",
		"_type":                  "Evaluation",
		"name":                   evalObjectID,
		"description":            "Cookbook evaluation definition (golang recipe 11)",
		"dataset":                objRef(dataset),
		"evaluate":               evalOpRefs["Evaluation.evaluate"],
		"predict_and_score":      evalOpRefs["Evaluation.predict_and_score"],
		"summarize":              evalOpRefs["Evaluation.summarize"],
		"scorers":                []string{opRef(scorer)},
		"trials":                 1,
		"evaluation_name":        nil,
		"metadata":               nil,
		"preprocess_model_input": nil,
	}
	created := post("/obj/create", map[string]any{
		"obj": map[string]any{
			"project_id":           projectID,
			"object_id":            evalObjectID,
			"val":                  evalVal,
			"builtin_object_class": "Evaluation",
		},
	})
	evalDigest := created["digest"].(string)
	evalRef := fmt.Sprintf("weave:///%s/object/%s:%s", projectID, evalObjectID, evalDigest)
	fmt.Printf("Published: %s digest=%s…\n", evalObjectID, evalDigest[:12])
	fmt.Printf("  ref: %s\n", evalRef)

	// 4) Tag + alias (recipe 07's pattern).
	tagsToAdd := []string{getenv("COOKBOOK_ENVIRONMENT", "dev"), "golang"}
	put(fmt.Sprintf("/objs/%s/versions/%s/tags", evalObjectID, evalDigest), map[string]any{
		"project_id": projectID,
		"tags":       tagsToAdd,
	})
	fmt.Printf("Tagged:    %v -> version %s…\n", tagsToAdd, evalDigest[:12])

	aliasesToSet := []string{"staging"}
	put(fmt.Sprintf("/objs/%s/aliases", evalObjectID), map[string]any{
		"project_id": projectID,
		"digest":     evalDigest,
		"aliases":    aliasesToSet,
	})
	fmt.Printf("Aliased:   %v -> version %s…\n", aliasesToSet, evalDigest[:12])

	// --- verification ---
	// Read the Eval Object back (with tags + aliases) and assert every ref +
	// metadata field round-trips. Retry until tags + aliases propagate.
	var obj map[string]any
	for i := 0; i < 8; i++ {
		r := post("/obj/read", map[string]any{
			"project_id":               projectID,
			"object_id":                evalObjectID,
			"digest":                   evalDigest,
			"include_tags_and_aliases": true,
		})
		obj, _ = r["obj"].(map[string]any)
		if obj != nil && containsAll(obj["tags"], tagsToAdd) && containsAll(obj["aliases"], aliasesToSet) {
			break
		}
		time.Sleep(time.Second)
	}
	if obj == nil {
		fatal("FAIL: Eval Object %s:%s not visible after 8 reads", evalObjectID, evalDigest)
	}

	val, _ := obj["val"].(map[string]any)
	if val["_class_name"] != "Evaluation" {
		fatal("_class_name: %v", val["_class_name"])
	}
	if val["dataset"] != objRef(dataset) {
		fatal("dataset: %v", val["dataset"])
	}
	for _, op := range canonicalOps {
		field := map[string]string{
			"Evaluation.evaluate":          "evaluate",
			"Evaluation.predict_and_score": "predict_and_score",
			"Evaluation.summarize":         "summarize",
		}[op.id]
		if val[field] != evalOpRefs[op.id] {
			fatal("%s: %v", field, val[field])
		}
	}
	scorers, _ := val["scorers"].([]any)
	if len(scorers) != 1 || scorers[0] != opRef(scorer) {
		fatal("scorers: %v", val["scorers"])
	}
	if obj["base_object_class"] != "Evaluation" {
		fatal("base_object_class: %v", obj["base_object_class"])
	}
	if !containsAll(obj["tags"], tagsToAdd) {
		fatal("tags: %v", obj["tags"])
	}
	if !containsAll(obj["aliases"], aliasesToSet) {
		fatal("aliases: %v", obj["aliases"])
	}
	fmt.Printf("Verified:  Eval Object refs + tags + aliases round-trip (tags=%v, aliases=%v)\n", obj["tags"], obj["aliases"])
}

// containsAll reports whether a JSON array (decoded as []any) holds every want.
func containsAll(arr any, want []string) bool {
	items, ok := arr.([]any)
	if !ok {
		return false
	}
	have := map[string]bool{}
	for _, it := range items {
		if s, ok := it.(string); ok {
			have[s] = true
		}
	}
	for _, w := range want {
		if !have[w] {
			return false
		}
	}
	return true
}
