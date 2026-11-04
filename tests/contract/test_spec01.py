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
            "account-export.schema.json": "examples/spec-01/account-export-local.json",
            "account-deletion.schema.json": "examples/spec-01/account-deletion-accepted.json",
        }
        for schema, file in samples.items():
            with self.subTest(file=file):
                validate(schema, file)
        validate("account-export.schema.json", "examples/spec-01/account-export-cloud.json")

    def test_cloud_export_cannot_claim_pending_mutations(self):
        value = json.loads((ROOT / "examples/spec-01/account-export-cloud.json").read_text(encoding="utf-8"))
        value["pending_mutation_ids"] = ["018f4400-1234-7000-8000-000000000041"]
        errors = list(Draft202012Validator(SCHEMAS["account-export.schema.json"], registry=REGISTRY).iter_errors(value))
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


if __name__ == "__main__":
    unittest.main()
