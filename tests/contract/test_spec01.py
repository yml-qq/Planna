"""Static SPEC-01 contract checks; no server or database is required."""

import json
import unittest
from pathlib import Path

import yaml
from jsonschema import Draft202012Validator, FormatChecker
from referencing import Registry, Resource


ROOT = Path(__file__).resolve().parents[2]
SCHEMAS = {
    path.name: json.loads(path.read_text(encoding="utf-8"))
    for path in (ROOT / "spec").glob("*.schema.json")
}
REGISTRY = Registry().with_resources(
    (schema["$id"], Resource.from_contents(schema)) for schema in SCHEMAS.values()
)
REGISTRY = REGISTRY.with_resource(
    "https://schemas.example.com/planna/task.schema.json",
    Resource.from_contents(SCHEMAS["task.schema.json"]),
)


def validate(name: str, file: str) -> None:
    schema = SCHEMAS[name]
    data = json.loads((ROOT / file).read_text(encoding="utf-8"))
    Draft202012Validator(schema, registry=REGISTRY, format_checker=FormatChecker()).validate(data)


class Spec01Contracts(unittest.TestCase):
    def test_schemas_are_valid_draft_2020_12(self):
        for schema in SCHEMAS.values():
            with self.subTest(schema=schema["$id"]):
                Draft202012Validator.check_schema(schema)

    def test_examples_match_versioned_schemas(self):
        samples = {
            "ai-plan.schema.json": "examples/ai-plan-preview.json",
            "ai-breakdown.schema.json": "examples/spec-01/ai-breakdown-preview.json",
            "account-export-v2.schema.json": "examples/spec-01/account-export-local.json",
            "account-deletion.schema.json": "examples/spec-01/account-deletion-accepted.json",
        }
        for schema, file in samples.items():
            with self.subTest(file=file):
                validate(schema, file)
        validate("account-export-v2.schema.json", "examples/spec-01/account-export-cloud.json")

    def test_cloud_export_cannot_claim_pending_mutations(self):
        value = json.loads((ROOT / "examples/spec-01/account-export-cloud.json").read_text(encoding="utf-8"))
        value["pending_mutation_ids"] = ["018f4400-1234-7000-8000-000000000041"]
        errors = list(Draft202012Validator(SCHEMAS["account-export-v2.schema.json"], registry=REGISTRY).iter_errors(value))
        self.assertTrue(errors)

    def test_breakdown_rejects_more_than_20_steps(self):
        value = json.loads((ROOT / "examples/spec-01/ai-breakdown-preview.json").read_text(encoding="utf-8"))
        value["steps"] = value["steps"] * 11
        errors = list(Draft202012Validator(SCHEMAS["ai-breakdown.schema.json"]).iter_errors(value))
        self.assertTrue(errors)

    def test_openapi_references_and_required_operations(self):
        api = yaml.safe_load((ROOT / "spec/openapi.yaml").read_text(encoding="utf-8"))
        self.assertEqual(api["openapi"], "3.1.0")
        for path, method in (
            ("/auth/step-up/challenges", "post"),
            ("/auth/step-up", "post"),
            ("/auth/login/challenges", "post"),
            ("/auth/login", "post"),
            ("/auth/refresh", "post"),
            ("/auth/devices", "get"),
            ("/auth/devices/{id}/revoke", "post"),
            ("/auth/logout", "post"),
            ("/ai/tasks/{id}/breakdowns", "post"),
            ("/ai/breakdowns/{id}", "put"),
            ("/ai/breakdowns/{id}/apply", "post"),
            ("/me/exports", "post"),
            ("/me/exports/{id}/download", "get"),
            ("/me/deletion-requests", "post"),
        ):
            with self.subTest(path=path):
                self.assertIn(method, api["paths"][path])

        def check_refs(value):
            if isinstance(value, dict):
                for key, item in value.items():
                    if key == "$ref":
                        if item.startswith("#/"):
                            target = api
                            for segment in item[2:].split("/"):
                                target = target[segment.replace("~1", "/").replace("~0", "~")]
                            self.assertIsNotNone(target)
                        else:
                            self.assertTrue((ROOT / "spec" / item).is_file(), item)
                    check_refs(item)
            elif isinstance(value, list):
                for item in value:
                    check_refs(item)

        check_refs(api)

    def test_writes_require_idempotency_and_plan_full_set(self):
        api = yaml.safe_load((ROOT / "spec/openapi.yaml").read_text(encoding="utf-8"))
        challenge = api["paths"]["/auth/login/challenges"]["post"]
        self.assertIn("202", challenge["responses"])
        self.assertIn("pending", challenge["responses"]["202"]["description"])
        self.assertIn("24 hours", challenge["description"])
        self.assertIn({"$ref": "#/components/parameters/DeviceId"}, challenge["parameters"])
        self.assertIn("Retry-After", challenge["responses"]["429"]["headers"])
        self.assertNotIn("503", challenge["responses"])
        login = api["paths"]["/auth/login"]["post"]
        self.assertIn({"$ref": "#/components/parameters/DeviceId"}, login["parameters"])
        self.assertIn("Retry-After", login["responses"]["429"]["headers"])
        self.assertIn("401", login["responses"])
        for path, method in (
            ("/auth/login/challenges", "post"),
            ("/auth/step-up/challenges", "post"),
            ("/ai/tasks/{id}/breakdowns", "post"),
            ("/ai/breakdowns/{id}", "put"),
            ("/ai/breakdowns/{id}/apply", "post"),
            ("/me/exports", "post"),
            ("/me/deletion-requests", "post"),
        ):
            params = api["paths"][path][method]["parameters"]
            self.assertIn({"$ref": "#/components/parameters/IdempotencyKey"}, params)
        apply = api["paths"]["/ai/plans/{id}/apply"]["post"]
        ids = apply["requestBody"]["content"]["application/json"]["schema"]["properties"]["accepted_operation_ids"]
        self.assertTrue(ids["uniqueItems"])
        self.assertEqual(ids["maxItems"], 20)
        self.assertIn("exact equality", ids["description"])

    def test_fault_cases_are_named(self):
        cases = json.loads((ROOT / "examples/spec-01/contract-cases.json").read_text(encoding="utf-8"))["cases"]
        ids = [case["id"] for case in cases]
        self.assertEqual(len(ids), len(set(ids)))
        for prefix in ("BREAKDOWN", "EXPORT", "DELETE", "PLAN"):
            self.assertTrue(any(item.startswith(prefix) for item in ids))

    def test_device_session_contract(self):
        api = yaml.safe_load((ROOT / "spec/openapi.yaml").read_text(encoding="utf-8"))
        listing = api["paths"]["/auth/devices"]["get"]
        self.assertEqual(set(listing["responses"]), {"200", "400", "401"})
        self.assertEqual(listing["responses"]["200"]["content"]["application/json"]["schema"]["$ref"],
                         "#/components/schemas/DeviceSessionPage")
        self.assertEqual({p["name"] for p in listing["parameters"]}, {"limit", "cursor"})
        for path in ("/auth/devices/{id}/revoke", "/auth/logout"):
            operation = api["paths"][path]["post"]
            self.assertIn({"$ref": "#/components/parameters/IdempotencyKey"}, operation["parameters"])
            self.assertIn({"$ref": "#/components/parameters/DeviceIfMatch"}, operation["parameters"])
            for status in ("204", "400", "401", "409"):
                self.assertIn(status, operation["responses"])
        self.assertIn("404", api["paths"]["/auth/devices/{id}/revoke"]["post"]["responses"])

    def test_task_read_contract(self):
        api = yaml.safe_load((ROOT / "spec/openapi.yaml").read_text(encoding="utf-8"))
        listing = api["paths"]["/tasks"]["get"]
        self.assertEqual({p["name"] for p in listing["parameters"]},
                         {"limit", "cursor", "list_id", "status", "tag_id"})
        self.assertEqual(set(listing["responses"]), {"200", "400", "401", "404"})
        page = listing["responses"]["200"]["content"]["application/json"]["schema"]["$ref"]
        self.assertEqual(page, "#/components/schemas/TaskPage")
        schema = api["components"]["schemas"]["TaskPage"]
        self.assertEqual(set(schema["required"]), {"items", "next_cursor"})
        self.assertEqual(schema["properties"]["items"]["items"]["$ref"], "#/components/schemas/Task")
        self.assertEqual(schema["properties"]["next_cursor"]["type"], ["string", "null"])
        params = {p["name"]: p for p in listing["parameters"]}
        self.assertEqual(params["limit"]["schema"]["minimum"], 1)
        self.assertEqual(params["limit"]["schema"]["maximum"], 200)
        self.assertEqual(params["limit"]["schema"]["default"], 50)
        self.assertEqual(params["list_id"]["schema"]["format"], "uuid")
        self.assertEqual(params["status"]["schema"]["enum"], ["todo", "in_progress", "done", "archived"])

    def test_task_update_contract(self):
        api = yaml.safe_load((ROOT / "spec/openapi.yaml").read_text(encoding="utf-8"))
        detail = api["paths"]["/tasks/{id}"]
        self.assertEqual(detail["parameters"], [{"$ref": "#/components/parameters/TaskId"}])
        self.assertEqual(set(detail["get"]["responses"]), {"200", "400", "401", "404"})
        patch = detail["patch"]
        self.assertEqual(set(patch["responses"]), {"200", "400", "401", "404", "409", "422"})
        self.assertIn({"$ref": "#/components/parameters/IdempotencyKey"}, patch["parameters"])
        patch_schema = api["components"]["schemas"]["UpdateTask"]
        self.assertTrue(patch_schema["additionalProperties"] is False)
        self.assertEqual(set(patch_schema["required"]), {"expected_version", "patch"})
        self.assertEqual(set(patch_schema["properties"]["patch"]["properties"]),
                         {"title", "description", "priority", "due_at",
                          "due_local_date", "time_zone", "estimate_minutes"})
        self.assertEqual(patch_schema["properties"]["patch"]["minProperties"], 1)
        delete = detail["delete"]
        self.assertEqual(set(delete["responses"]), {"204", "400", "401", "404", "409"})
        params = {}
        for p in delete["parameters"]:
            name = p["$ref"].rsplit("/", 1)[-1] if "$ref" in p else p["name"]
            params[name] = p
        self.assertEqual(params["IdempotencyKey"]["$ref"], "#/components/parameters/IdempotencyKey")
        self.assertEqual(params["If-Match"]["schema"]["pattern"], '^"v[0-9]+"$')
        self.assertIn("changed_fields", patch["description"])

    def test_task_complete_contract(self):
        api = yaml.safe_load((ROOT / "spec/openapi.yaml").read_text(encoding="utf-8"))
        for path in ("/tasks/{id}/complete", "/tasks/{id}/reopen"):
            operation = api["paths"][path]["post"]
            self.assertEqual(set(operation["responses"]), {"200", "400", "401", "404", "409", "422"})
            params = {p["$ref"] if "$ref" in p else p.get("name"): p for p in operation["parameters"]}
            self.assertIn("#/components/parameters/IdempotencyKey", params)
            self.assertIn("#/components/parameters/TaskId", params)
            body = operation["requestBody"]["content"]["application/json"]["schema"]
            self.assertEqual(set(body["required"]), {"expected_version"})
            self.assertFalse(body["additionalProperties"])
        complete = api["paths"]["/tasks/{id}/complete"]["post"]
        props = complete["requestBody"]["content"]["application/json"]["schema"]["properties"]
        self.assertEqual(props["occurrence_id"]["type"], ["string", "null"])
        self.assertEqual(props["completed_at"]["type"], ["string", "null"])
        self.assertIn("ALREADY_COMPLETED", complete["description"])
        self.assertIn("clamped", complete["description"])
        completion_schema = complete["responses"]["200"]["content"]["application/json"]["schema"]
        self.assertEqual({item["$ref"] for item in completion_schema["oneOf"]},
                         {"#/components/schemas/Task", "#/components/schemas/Occurrence"})
        reopen = api["paths"]["/tasks/{id}/reopen"]["post"]
        self.assertIn("occurrence_id", reopen["requestBody"]["content"]["application/json"]["schema"]["properties"])
        reopen_schema = reopen["responses"]["200"]["content"]["application/json"]["schema"]
        self.assertEqual({item["$ref"] for item in reopen_schema["oneOf"]},
                         {"#/components/schemas/Task", "#/components/schemas/Occurrence"})
        reopen = api["paths"]["/tasks/{id}/reopen"]["post"]
        self.assertIn("NOT_COMPLETED", reopen["description"])

    def test_list_contract(self):
        api = yaml.safe_load((ROOT / "spec/openapi.yaml").read_text(encoding="utf-8"))
        listing = api["paths"]["/lists"]["get"]
        self.assertEqual(set(listing["responses"]), {"200", "401"})
        items = listing["responses"]["200"]["content"]["application/json"]["schema"]["properties"]["items"]["items"]
        self.assertEqual(items["$ref"], "#/components/schemas/TaskList")
        create = api["paths"]["/lists"]["post"]
        self.assertEqual(set(create["responses"]), {"201", "400", "401", "409", "422"})
        self.assertIn({"$ref": "#/components/parameters/IdempotencyKey"}, create["parameters"])
        body = create["requestBody"]["content"]["application/json"]["schema"]
        self.assertFalse(body["additionalProperties"])
        self.assertEqual(set(body["required"]), {"id", "name"})
        detail = api["paths"]["/lists/{id}"]
        self.assertEqual(set(detail["get"]["responses"]), {"200", "400", "401", "404"})
        patch = detail["patch"]
        self.assertEqual(set(patch["responses"]), {"200", "400", "401", "404", "409", "422"})
        self.assertIn({"$ref": "#/components/parameters/IdempotencyKey"}, patch["parameters"])
        patch_body = patch["requestBody"]["content"]["application/json"]["schema"]
        self.assertEqual(set(patch_body["required"]), {"expected_version", "patch"})
        self.assertEqual(set(patch_body["properties"]["patch"]["required"]), {"name"})
        delete = detail["delete"]
        self.assertEqual(set(delete["responses"]), {"204", "400", "401", "404", "409", "422"})
        self.assertIn("LIST_NOT_EMPTY", delete["description"])
        self.assertIn("LIST_INBOX_PROTECTED", delete["description"])
        schema = api["components"]["schemas"]["TaskList"]
        self.assertEqual(set(schema["required"]), {"id", "name", "kind", "version"})
        self.assertEqual(schema["properties"]["kind"]["enum"], ["inbox", "custom"])

    def test_tag_contract(self):
        api = yaml.safe_load((ROOT / "spec/openapi.yaml").read_text(encoding="utf-8"))
        listing = api["paths"]["/tags"]["get"]
        self.assertEqual(set(listing["responses"]), {"200", "401"})
        items = listing["responses"]["200"]["content"]["application/json"]["schema"]["properties"]["items"]["items"]
        self.assertEqual(items["$ref"], "#/components/schemas/Tag")
        create = api["paths"]["/tags"]["post"]
        self.assertEqual(set(create["responses"]), {"201", "400", "401", "409", "422"})
        self.assertIn({"$ref": "#/components/parameters/IdempotencyKey"}, create["parameters"])
        body = create["requestBody"]["content"]["application/json"]["schema"]
        self.assertFalse(body["additionalProperties"])
        self.assertEqual(set(body["required"]), {"id", "name"})
        detail = api["paths"]["/tags/{id}"]
        self.assertEqual(set(detail["get"]["responses"]), {"200", "400", "401", "404"})
        patch = detail["patch"]
        self.assertEqual(set(patch["responses"]), {"200", "400", "401", "404", "409", "422"})
        patch_schema = patch["requestBody"]["content"]["application/json"]["schema"]
        self.assertEqual(set(patch_schema["required"]), {"expected_version", "patch"})
        self.assertEqual(patch_schema["properties"]["patch"]["minProperties"], 1)
        self.assertIn("TAG_NAME_CONFLICT", patch["description"])
        delete = detail["delete"]
        self.assertEqual(set(delete["responses"]), {"204", "400", "401", "404", "409"})
        params = {}
        for p in delete["parameters"]:
            name = p["$ref"].rsplit("/", 1)[-1] if "$ref" in p else p["name"]
            params[name] = p
        self.assertEqual(params["If-Match"]["schema"]["pattern"], '^"v[0-9]+"$')
        schema = api["components"]["schemas"]["Tag"]
        self.assertEqual(set(schema["required"]), {"id", "name", "version"})
        self.assertEqual(schema["properties"]["color"]["type"], ["string", "null"])

    def test_task_tags_contract(self):
        api = yaml.safe_load((ROOT / "spec/openapi.yaml").read_text(encoding="utf-8"))
        detail = api["paths"]["/tasks/{id}/tags"]
        self.assertEqual(detail["parameters"], [{"$ref": "#/components/parameters/TaskId"}])
        self.assertEqual(set(detail["get"]["responses"]), {"200", "400", "401", "404"})
        put = detail["put"]
        self.assertEqual(set(put["responses"]), {"200", "400", "401", "404", "409", "422"})
        self.assertIn({"$ref": "#/components/parameters/IdempotencyKey"}, put["parameters"])
        body = put["requestBody"]["content"]["application/json"]["schema"]
        self.assertFalse(body["additionalProperties"])
        self.assertEqual(set(body["required"]), {"expected_version", "tag_ids"})
        self.assertEqual(body["properties"]["tag_ids"]["maxItems"], 20)
        self.assertTrue(body["properties"]["tag_ids"]["uniqueItems"])
        set_schema = api["components"]["schemas"]["TaskTagSet"]
        self.assertEqual(set(set_schema["required"]), {"task_id", "version", "tag_ids"})

    def test_step_contract(self):
        api = yaml.safe_load((ROOT / "spec/openapi.yaml").read_text(encoding="utf-8"))
        task_steps = api["paths"]["/tasks/{id}/steps"]
        self.assertEqual(task_steps["parameters"], [{"$ref": "#/components/parameters/TaskId"}])
        self.assertEqual(set(task_steps["get"]["responses"]), {"200", "400", "401", "404"})
        post = task_steps["post"]
        self.assertEqual(set(post["responses"]), {"201", "400", "401", "404", "409", "422"})
        self.assertIn({"$ref": "#/components/parameters/IdempotencyKey"}, post["parameters"])
        body = post["requestBody"]["content"]["application/json"]["schema"]
        self.assertFalse(body["additionalProperties"])
        self.assertEqual(set(body["required"]), {"id", "title"})
        step = api["paths"]["/steps/{id}"]
        patch = step["patch"]
        self.assertEqual(set(patch["responses"]), {"200", "400", "401", "404", "409", "422"})
        patch_body = patch["requestBody"]["content"]["application/json"]["schema"]
        self.assertEqual(set(patch_body["required"]), {"expected_version", "patch"})
        self.assertEqual(set(patch_body["properties"]["patch"]["properties"]),
                         {"title", "is_done", "position", "estimate_minutes"})
        delete = step["delete"]
        self.assertEqual(set(delete["responses"]), {"204", "400", "401", "404", "409"})
        params = {}
        for p in delete["parameters"]:
            name = p["$ref"].rsplit("/", 1)[-1] if "$ref" in p else p["name"]
            params[name] = p
        self.assertEqual(params["If-Match"]["schema"]["pattern"], '^"v[0-9]+"$')
        schema = api["components"]["schemas"]["TaskStep"]
        self.assertEqual(set(schema["required"]), {"id", "title", "is_done", "position", "version"})

    def test_recurrence_contract(self):
        api = yaml.safe_load((ROOT / "spec/openapi.yaml").read_text(encoding="utf-8"))
        rule = api["paths"]["/tasks/{id}/recurrence"]
        self.assertEqual(set(rule["get"]["responses"]), {"200", "400", "401", "404"})
        put = rule["put"]
        self.assertEqual(set(put["responses"]), {"200", "400", "401", "404", "409", "422"})
        self.assertIn({"$ref": "#/components/parameters/IdempotencyKey"}, put["parameters"])
        body = put["requestBody"]["content"]["application/json"]["schema"]
        self.assertFalse(body["additionalProperties"])
        self.assertEqual(set(body["required"]),
                         {"expected_version", "kind", "time_zone", "dtstart_local"})
        self.assertEqual(body["properties"]["kind"]["enum"], ["rrule", "after_completion"])
        self.assertEqual(body["properties"]["expected_version"]["minimum"], 0)
        self.assertEqual(body["properties"]["interval_days"]["maximum"], 3650)
        recurrence = api["components"]["schemas"]["Recurrence"]
        self.assertEqual(set(recurrence["required"]),
                         {"task_id", "kind", "time_zone", "dtstart_local", "version"})
        self.assertEqual(recurrence["properties"]["kind"]["enum"], ["rrule", "after_completion"])

        delete = rule["delete"]
        self.assertEqual(set(delete["responses"]), {"204", "400", "401", "404", "409"})
        occurrences = api["paths"]["/tasks/{id}/occurrences"]["get"]
        self.assertEqual(set(occurrences["responses"]), {"200", "400", "401", "404"})
        params = {p["name"]: p for p in occurrences["parameters"]}
        self.assertEqual(set(params), {"from", "to"})
        schema = api["components"]["schemas"]["Occurrence"]
        self.assertEqual(set(schema["required"]),
                         {"id", "task_id", "original_local_start", "time_zone", "status", "version"})
        self.assertEqual(schema["properties"]["status"]["enum"], ["todo", "done", "skipped"])

    def test_ai_parse_contract(self):
        api = yaml.safe_load((ROOT / "spec/openapi.yaml").read_text(encoding="utf-8"))
        parse = api["paths"]["/ai/parse"]["post"]
        self.assertEqual(set(parse["responses"]), {"200", "400", "401", "403", "429", "503"})
        self.assertIn("AI_UNAVAILABLE", parse["description"])
        request = api["components"]["schemas"]["AiParseRequest"]
        self.assertEqual(set(request["required"]), {"input", "context"})
        self.assertEqual(set(request["properties"]["context"]["required"]), {"time_zone", "now"})
        response = api["components"]["schemas"]["AiParseResponse"]
        self.assertEqual(set(response["required"]),
                         {"intent", "drafts", "assumptions", "unresolved_fields"})
        draft = response["properties"]["drafts"]["items"]
        self.assertEqual(set(draft["required"]), {"title"})
        self.assertEqual(response["properties"]["intent"]["enum"],
                         ["create_task", "multi_task", "clarify", "unsupported"])

    def test_ai_breakdown_contract(self):
        api = yaml.safe_load((ROOT / "spec/openapi.yaml").read_text(encoding="utf-8"))
        create = api["paths"]["/ai/tasks/{id}/breakdowns"]["post"]
        self.assertEqual(set(create["responses"]), {"201", "400", "401", "403", "404", "409", "429", "503"})
        self.assertIn({"$ref": "#/components/parameters/IdempotencyKey"}, create["parameters"])
        body = create["requestBody"]["content"]["application/json"]["schema"]
        self.assertEqual(set(body["required"]), {"expected_version"})
        self.assertFalse(body["additionalProperties"])
        edit = api["paths"]["/ai/breakdowns/{id}"]["put"]
        self.assertEqual(set(edit["responses"]), {"200", "400", "401", "403", "404", "409", "422", "429"})
        edit_body = edit["requestBody"]["content"]["application/json"]["schema"]
        self.assertEqual(edit_body["properties"]["steps"]["maxItems"], 20)
        self.assertEqual(edit_body["properties"]["steps"]["minItems"], 1)
        apply_op = api["paths"]["/ai/breakdowns/{id}/apply"]["post"]
        self.assertEqual(set(apply_op["responses"]), {"200", "400", "401", "403", "404", "409", "429"})
        applied = apply_op["responses"]["200"]["content"]["application/json"]["schema"]
        self.assertEqual(set(applied["required"]), {"status", "task_id", "step_ids"})
        self.assertEqual(applied["properties"]["step_ids"]["maxItems"], 20)
        self.assertIn("same idempotency key", apply_op["responses"]["200"]["description"])

    def test_ai_plan_contract(self):
        api = yaml.safe_load((ROOT / "spec/openapi.yaml").read_text(encoding="utf-8"))
        propose = api["paths"]["/ai/plans"]["post"]
        self.assertEqual(set(propose["responses"]), {"200", "400", "401", "403", "404", "422", "429"})
        body = propose["requestBody"]["content"]["application/json"]["schema"]
        self.assertEqual(set(body["required"]), {"task_ids", "range_start", "range_end"})
        self.assertEqual(body["properties"]["task_ids"]["maxItems"], 100)
        self.assertTrue(body["properties"]["task_ids"]["uniqueItems"])
        apply_op = api["paths"]["/ai/plans/{id}/apply"]["post"]
        self.assertEqual(set(apply_op["responses"]), {"200", "400", "401", "403", "404", "409", "429"})
        self.assertIn({"$ref": "#/components/parameters/IdempotencyKey"}, apply_op["parameters"])
        apply_body = apply_op["requestBody"]["content"]["application/json"]["schema"]
        self.assertEqual(set(apply_body["required"]),
                         {"snapshot_hash", "accepted_operation_ids", "confirmation_token"})
        accepted = apply_body["properties"]["accepted_operation_ids"]
        self.assertTrue(accepted["uniqueItems"])
        self.assertEqual(accepted["maxItems"], 20)
        self.assertIn("exact equality", accepted["description"])

    def test_export_contract(self):
        api = yaml.safe_load((ROOT / "spec/openapi.yaml").read_text(encoding="utf-8"))
        create = api["paths"]["/me/exports"]["post"]
        self.assertEqual(set(create["responses"]), {"202", "400", "401", "409"})
        self.assertIn({"$ref": "#/components/parameters/IdempotencyKey"}, create["parameters"])
        listing = api["paths"]["/me/exports"]["get"]
        self.assertEqual(set(listing["responses"]), {"200", "401"})
        get = api["paths"]["/me/exports/{id}"]["get"]
        self.assertEqual(set(get["responses"]), {"200", "400", "401", "404"})
        download = api["paths"]["/me/exports/{id}/download"]["get"]
        self.assertEqual(set(download["responses"]), {"200", "400", "401", "404", "409", "410"})
        self.assertIn("EXPORT_EXPIRED", download["description"])
        schema_ref = download["responses"]["200"]["content"]["application/json"]["schema"]["$ref"]
        self.assertEqual(schema_ref, "./account-export-v2.schema.json")
        job = api["components"]["schemas"]["AccountExportJob"]
        self.assertEqual(set(job["required"]),
                         {"export_id", "status", "requested_at", "snapshot_at", "expires_at"})

    def test_step_up_contract(self):
        api = yaml.safe_load((ROOT / "spec/openapi.yaml").read_text(encoding="utf-8"))
        challenge = api["paths"]["/auth/step-up/challenges"]["post"]
        self.assertEqual(set(challenge["responses"]), {"202", "400", "401", "422", "429"})
        self.assertIn({"$ref": "#/components/parameters/IdempotencyKey"}, challenge["parameters"])
        verify = api["paths"]["/auth/step-up"]["post"]
        self.assertEqual(set(verify["responses"]), {"200", "400", "401", "429"})
        body = verify["requestBody"]["content"]["application/json"]["schema"]
        self.assertEqual(set(body["required"]), {"purpose", "code"})
        self.assertEqual(body["properties"]["code"]["minLength"], 6)
        token = verify["responses"]["200"]["content"]["application/json"]["schema"]
        self.assertEqual(set(token["required"]), {"confirmation_token", "expires_at"})
        self.assertEqual(token["properties"]["confirmation_token"]["minLength"], 16)
        self.assertIn("account-deletion", challenge["description"])

    def test_deletion_contract(self):
        api = yaml.safe_load((ROOT / "spec/openapi.yaml").read_text(encoding="utf-8"))
        deletion = api["paths"]["/me/deletion-requests"]["post"]
        self.assertEqual(set(deletion["responses"]), {"202", "400", "401", "403", "409", "422"})
        self.assertIn({"$ref": "#/components/parameters/IdempotencyKey"}, deletion["parameters"])
        body = deletion["requestBody"]["content"]["application/json"]["schema"]
        self.assertEqual(set(body["required"]), {"confirmation_token", "acknowledge_offline_devices"})
        self.assertFalse(body["properties"]["acknowledge_offline_devices"]["const"] is False)
        schema = json.loads((ROOT / "spec/account-deletion.schema.json").read_text(encoding="utf-8"))
        self.assertEqual(set(schema["required"]),
                         {"deletion_request_id", "status", "requested_at", "policy_version"})
        self.assertEqual(schema["properties"]["status"]["const"], "accepted")
        response_ref = deletion["responses"]["202"]["content"]["application/json"]["schema"]["$ref"]
        self.assertEqual(response_ref, "./account-deletion.schema.json")

    def test_habit_contract(self):
        api = yaml.safe_load((ROOT / "spec/openapi.yaml").read_text(encoding="utf-8"))
        habits = api["paths"]["/habits"]
        self.assertEqual(set(habits["get"]["responses"]), {"200", "401"})
        post = habits["post"]
        self.assertEqual(set(post["responses"]), {"201", "400", "401", "409", "422"})
        self.assertIn({"$ref": "#/components/parameters/IdempotencyKey"}, post["parameters"])
        body = post["requestBody"]["content"]["application/json"]["schema"]
        self.assertEqual(set(body["required"]), {"title", "schedule_type", "time_zone"})
        self.assertEqual(body["properties"]["schedule_type"]["enum"], ["daily", "weekly", "weekdays"])
        self.assertEqual(body["properties"]["weekdays"]["maxItems"], 7)
        habit = api["components"]["schemas"]["Habit"]
        self.assertEqual(set(habit["required"]),
                         {"id", "title", "schedule_type", "target_quantity", "unit", "time_zone",
                          "version", "created_at", "updated_at"})
        self.assertEqual(habit["properties"]["schedule_type"]["enum"], ["daily", "weekly", "weekdays"])
        by_id = api["paths"]["/habits/{id}"]
        self.assertEqual(set(by_id["get"]["responses"]), {"200", "400", "401", "404"})
        patch = by_id["patch"]
        self.assertEqual(set(patch["responses"]), {"200", "400", "401", "404", "409", "422"})
        patch_body = patch["requestBody"]["content"]["application/json"]["schema"]
        self.assertEqual(set(patch_body["required"]), {"expected_version", "patch"})
        self.assertEqual(api["components"]["schemas"]["HabitPatch"]["minProperties"], 1)
        delete = by_id["delete"]
        self.assertEqual(set(delete["responses"]), {"204", "400", "401", "404", "409"})
        if_match = {parameter["name"]: parameter for parameter in delete["parameters"]
                    if parameter.get("name") == "If-Match"}
        self.assertEqual(if_match["If-Match"]["schema"]["pattern"], '^"v[0-9]+"$')

    def test_habit_log_contract(self):
        api = yaml.safe_load((ROOT / "spec/openapi.yaml").read_text(encoding="utf-8"))
        logs = api["paths"]["/habits/{id}/logs"]["get"]
        self.assertEqual(set(logs["responses"]), {"200", "400", "401", "404", "422"})
        params = {parameter["name"]: parameter for parameter in logs["parameters"]}
        self.assertEqual(set(params), {"id", "from", "to"})
        self.assertEqual(params["from"]["schema"]["format"], "date")
        self.assertTrue(params["from"]["required"])
        upsert = api["paths"]["/habits/{id}/logs/{date}"]["put"]
        self.assertEqual(set(upsert["responses"]), {"200", "400", "401", "404", "409", "422"})
        self.assertIn({"$ref": "#/components/parameters/IdempotencyKey"}, upsert["parameters"])
        body = upsert["requestBody"]["content"]["application/json"]["schema"]
        self.assertFalse(body["additionalProperties"])
        self.assertEqual(body["properties"]["status"]["enum"], ["done", "skipped"])
        self.assertEqual(body["properties"]["quantity"]["maximum"], 1000)
        streak = api["paths"]["/habits/{id}/streak"]["get"]
        self.assertEqual(set(streak["responses"]), {"200", "400", "401", "404", "422"})
        streak_params = {parameter["name"]: parameter for parameter in streak["parameters"]}
        self.assertEqual(set(streak_params), {"id", "through"})
        self.assertTrue(streak_params["through"]["required"])
        streak_schema = api["components"]["schemas"]["HabitStreak"]
        self.assertEqual(set(streak_schema["required"]),
                         {"habit_id", "through", "current_streak", "longest_streak", "target_quantity"})
        log = api["components"]["schemas"]["HabitLog"]
        self.assertEqual(set(log["required"]),
                         {"id", "habit_id", "log_date", "quantity", "status", "version",
                          "created_at", "updated_at"})
        self.assertEqual(log["properties"]["status"]["enum"], ["done", "skipped"])

    def test_ai_consent_contract(self):
        api = yaml.safe_load((ROOT / "spec/openapi.yaml").read_text(encoding="utf-8"))
        consent = api["paths"]["/ai/consent"]
        self.assertEqual(set(consent["get"]["responses"]), {"200", "401", "429"})
        put = consent["put"]
        self.assertEqual(set(put["responses"]), {"200", "400", "401", "409", "422", "429"})
        self.assertIn({"$ref": "#/components/parameters/IdempotencyKey"}, put["parameters"])
        body = put["requestBody"]["content"]["application/json"]["schema"]
        self.assertEqual(set(body["required"]), {"allowed_scopes"})
        scopes = body["properties"]["allowed_scopes"]
        self.assertEqual(scopes["maxItems"], 4)
        self.assertTrue(scopes["uniqueItems"])
        self.assertEqual(api["components"]["schemas"]["AiConsentScope"]["enum"],
                         ["task_drafts", "plan_preview", "task_notes", "calendar_free_slots"])
        view = api["components"]["schemas"]["AiConsent"]
        self.assertEqual(set(view["required"]), {"allowed_scopes", "updated_at"})

    def test_sync_pull_contract(self):
        api = yaml.safe_load((ROOT / "spec/openapi.yaml").read_text(encoding="utf-8"))
        pull = api["paths"]["/sync/pull"]["get"]
        self.assertEqual({p["name"] for p in pull["parameters"]}, {"cursor", "limit"})
        self.assertEqual(set(pull["responses"]), {"200", "400", "401", "410"})
        params = {p["name"]: p for p in pull["parameters"]}
        self.assertEqual(params["limit"]["schema"]["maximum"], 200)
        self.assertEqual(params["limit"]["schema"]["default"], 100)
        self.assertIn("CURSOR_EXPIRED", pull["description"])
        response = api["components"]["schemas"]["SyncPullResponse"]
        self.assertEqual(set(response["required"]), {"changes", "next_cursor", "has_more", "server_time"})
        change = api["components"]["schemas"]["SyncChange"]
        self.assertEqual(set(change["required"]),
                         {"revision", "change_index", "entity_type", "entity_id", "op", "version"})
        self.assertEqual(change["properties"]["payload"]["type"], ["object", "null"])

    def test_sync_push_contract(self):
        api = yaml.safe_load((ROOT / "spec/openapi.yaml").read_text(encoding="utf-8"))
        push = api["paths"]["/sync/push"]["post"]
        self.assertEqual(set(push["responses"]), {"200", "400", "401"})
        request = api["components"]["schemas"]["SyncPushRequest"]
        self.assertEqual(set(request["required"]), {"device_id", "mutations"})
        self.assertEqual(request["properties"]["mutations"]["maxItems"], 100)
        self.assertEqual(request["properties"]["mutations"]["minItems"], 1)
        mutation = api["components"]["schemas"]["SyncMutation"]
        self.assertEqual(set(mutation["required"]),
                         {"mutation_id", "entity_type", "entity_id", "op", "base_version", "payload"})
        self.assertEqual(mutation["properties"]["op"]["enum"], ["upsert", "delete"])
        response = api["components"]["schemas"]["SyncPushResponse"]
        self.assertEqual(set(response["required"]), {"results"})
        result = response["properties"]["results"]["items"]
        self.assertEqual(set(result["required"]), {"mutation_id", "status"})
        self.assertEqual(result["properties"]["status"]["enum"],
                         ["accepted", "duplicate", "conflict", "rejected"])

    def test_sync_bootstrap_contract(self):
        api = yaml.safe_load((ROOT / "spec/openapi.yaml").read_text(encoding="utf-8"))
        bootstrap = api["paths"]["/sync/bootstrap"]["post"]
        self.assertEqual(set(bootstrap["responses"]), {"200", "401"})
        self.assertIn("snapshot_cursor", bootstrap["description"])
        schema = bootstrap["responses"]["200"]["content"]["application/json"]["schema"]
        self.assertEqual(set(schema["required"]), {"snapshot_cursor", "tasks", "lists"})
        self.assertEqual(schema["properties"]["tasks"]["items"]["$ref"], "#/components/schemas/Task")
        self.assertEqual(schema["properties"]["lists"]["items"]["$ref"], "#/components/schemas/TaskList")
        self.assertEqual(schema["properties"]["next_page_token"]["type"], ["string", "null"])


if __name__ == "__main__":
    unittest.main()
