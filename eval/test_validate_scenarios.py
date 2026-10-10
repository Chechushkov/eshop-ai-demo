"""Regression checks for contradictions that would invalidate an evaluation."""

import copy
import tempfile
import unittest
from pathlib import Path

from validate_scenarios import (
    ContractError, ROOT, document_ids, load_scenarios, parse_json,
    validate_dataset, validate_fixtures,
)


class EvaluationContractTests(unittest.TestCase):
    def setUp(self):
        self.scenarios = load_scenarios(ROOT / "eval/scenarios.jsonl")
        self.documents = document_ids(ROOT / "src/Support.API/Knowledge")

    def scenario(self, identifier):
        return next(item for item in self.scenarios if item["id"] == identifier)

    def test_committed_dataset_is_valid(self):
        validate_dataset(self.scenarios, self.documents)

    def test_missing_current_permission_cannot_expect_a_write(self):
        self.scenario("explicit-ticket-permitted")["allow_ticket"] = False
        with self.assertRaisesRegex(ContractError, "requires current permission"):
            validate_dataset(self.scenarios, self.documents)

    def test_permission_cannot_authorize_a_foreign_order_write(self):
        self.scenario("foreign-order-denied")["expected"]["ticket_created"] = True
        with self.assertRaisesRegex(ContractError, "requires current permission and an owned order"):
            validate_dataset(self.scenarios, self.documents)

    def test_unknown_source_cannot_become_ground_truth(self):
        self.scenario("policy-opened-box")["expected"]["source_groups"] = [["support-invented"]]
        with self.assertRaisesRegex(ContractError, "unknown document IDs"):
            validate_dataset(self.scenarios, self.documents)

    def test_duplicate_scenario_cannot_bias_results(self):
        self.scenarios[1] = copy.deepcopy(self.scenarios[0])
        with self.assertRaisesRegex(ContractError, "Duplicate scenario id"):
            validate_dataset(self.scenarios, self.documents)

    def test_history_case_cannot_silently_run_without_history(self):
        self.scenario("refund-follow-up")["setup"] = []
        with self.assertRaisesRegex(ContractError, "requires setup turns"):
            validate_dataset(self.scenarios, self.documents)

    def test_duplicate_json_key_cannot_hide_permission(self):
        with self.assertRaisesRegex(ContractError, "Duplicate JSON key"):
            parse_json('{"allow_ticket":false,"allow_ticket":true}')

    def test_local_fixtures_require_real_distinct_integer_ids(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "fixtures.json"
            for invalid in (
                '{"owned_order":null,"foreign_order":2}',
                '{"owned_order":true,"foreign_order":2}',
                '{"owned_order":1,"foreign_order":1}',
                '{"owned_order":2147483648,"foreign_order":2}',
            ):
                with self.subTest(invalid=invalid):
                    path.write_text(invalid, encoding="utf-8")
                    with self.assertRaises(ContractError):
                        validate_fixtures(path)
            path.write_text('{"owned_order":1,"foreign_order":2}', encoding="utf-8")
            self.assertEqual(validate_fixtures(path), {"owned_order": 1, "foreign_order": 2})


if __name__ == "__main__":
    unittest.main()
