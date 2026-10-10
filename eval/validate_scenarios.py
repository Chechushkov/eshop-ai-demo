#!/usr/bin/env python3
"""Validate the evaluation contract without calling an API or a model."""

import argparse
import json
import re
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parent.parent
CATEGORIES = {
    "general_policy", "owned_order", "explicit_ticket", "human_action",
    "permission_without_need", "ticket_without_permission", "multiple_topics",
    "missing_knowledge", "foreign_order", "history_follow_up",
}
ORDER_REFS = {"owned_order", "foreign_order"}
TOOLS = {"SearchKnowledge", "GetOrderStatus", "CreateSupportTicket"}
NO_TICKET_SENTENCE = "Новая заявка не создана."
SCENARIO_FIELDS = {
    "schema_version", "id", "category", "question", "order_ref",
    "allow_ticket", "setup", "expected",
}
EXPECTED_FIELDS = {
    "order_found", "ticket_created", "required_tools", "source_groups",
    "must_include", "must_not_include", "required_facts", "forbidden_claims",
}
SETUP_FIELDS = {"question", "order_ref", "allow_ticket", "expected_ticket_created"}


class ContractError(ValueError):
    """The dataset cannot be used for a comparable evaluation run."""


def require(condition, message):
    if not condition:
        raise ContractError(message)


def exact_fields(value, fields, label):
    require(isinstance(value, dict), f"{label}: expected an object")
    missing = fields - value.keys()
    extra = value.keys() - fields
    require(not missing and not extra,
            f"{label}: missing fields {sorted(missing)}; unknown fields {sorted(extra)}")


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, f"Duplicate JSON key: {key}")
        result[key] = value
    return result


def parse_json(text):
    try:
        return json.loads(text, object_pairs_hook=unique_object)
    except json.JSONDecodeError as error:
        raise ContractError(f"Invalid JSON: {error.msg}") from error


def load_scenarios(path):
    scenarios = []
    # Accept an editor-added UTF-8 BOM without changing the scenario content.
    for number, line in enumerate(path.read_text(encoding="utf-8-sig").splitlines(), 1):
        require(bool(line.strip()), f"Line {number}: blank JSONL records are not allowed")
        try:
            scenarios.append(parse_json(line))
        except ContractError as error:
            raise ContractError(f"Line {number}: {error}") from error
    return scenarios


def document_ids(directory):
    require(directory.is_dir(), f"Knowledge directory does not exist: {directory}")
    # Match KnowledgeRepository's file-name-based document IDs; chunk IDs vary.
    ids = {"support-" + path.stem for path in directory.glob("*.md")}
    require(bool(ids), "The knowledge directory contains no Markdown documents")
    return ids


def text_list(value, label, nonempty=False):
    require(isinstance(value, list), f"{label}: expected an array")
    require(all(isinstance(item, str) and item.strip() for item in value),
            f"{label}: expected nonblank strings")
    require(len(value) == len(set(value)), f"{label}: duplicate entries")
    require(not nonempty or bool(value), f"{label}: must not be empty")


def validate_input(value, label):
    question = value["question"]
    require(isinstance(question, str) and bool(question.strip()) and len(question) <= 2000,
            f"{label}: question must contain 1 to 2000 characters")
    reference = value["order_ref"]
    require(reference is None or isinstance(reference, str) and reference in ORDER_REFS,
            f"{label}: order_ref must be null, owned_order or foreign_order")
    require(type(value["allow_ticket"]) is bool, f"{label}: allow_ticket must be a boolean")
    require(not value["allow_ticket"] or reference is not None,
            f"{label}: this dataset requires a selected order when permission is enabled")


def validate_ticket(created, value, label):
    require(type(created) is bool, f"{label}: ticket_created must be a boolean")
    require(not created or value["allow_ticket"] and value["order_ref"] == "owned_order",
            f"{label}: a ticket expectation requires current permission and an owned order")


def validate_dataset(scenarios, known_documents, expected_count=10):
    require(isinstance(scenarios, list), "Scenarios must be an array")
    require(len(scenarios) == expected_count,
            f"Expected {expected_count} scenarios, received {len(scenarios)}")
    seen = set()
    categories = set()
    for position, scenario in enumerate(scenarios, 1):
        label = f"Scenario {position}"
        exact_fields(scenario, SCENARIO_FIELDS, label)
        require(type(scenario["schema_version"]) is int and scenario["schema_version"] == 1,
                f"{label}: schema_version must be 1")
        identifier = scenario["id"]
        require(isinstance(identifier, str) and re.fullmatch(r"[a-z0-9]+(?:-[a-z0-9]+)*", identifier),
                f"{label}: invalid id")
        require(identifier not in seen, f"Duplicate scenario id: {identifier}")
        seen.add(identifier)
        label = identifier
        category = scenario["category"]
        require(isinstance(category, str) and category in CATEGORIES,
                f"{label}: unknown category")
        categories.add(category)
        validate_input(scenario, label)
        setup = scenario["setup"]
        require(isinstance(setup, list), f"{label}: setup must be an array")
        for index, turn in enumerate(setup, 1):
            turn_label = f"{label} setup {index}"
            exact_fields(turn, SETUP_FIELDS, turn_label)
            validate_input(turn, turn_label)
            validate_ticket(turn["expected_ticket_created"], turn, turn_label)
        require(category != "history_follow_up" or bool(setup),
                f"{label}: a history follow-up requires setup turns")
        expected = scenario["expected"]
        exact_fields(expected, EXPECTED_FIELDS, f"{label} expected")
        validate_ticket(expected["ticket_created"], scenario, label)
        order_found = expected["order_found"]
        require(order_found is None or type(order_found) is bool,
                f"{label}: order_found must be null or a boolean")
        expected_found = {None: None, "owned_order": True, "foreign_order": False}
        require(order_found is expected_found[scenario["order_ref"]],
                f"{label}: order_found contradicts the selected fixture")
        for field in ("required_tools", "must_include", "must_not_include",
                      "required_facts", "forbidden_claims"):
            text_list(expected[field], f"{label} {field}",
                      nonempty=field in {"required_tools", "required_facts", "forbidden_claims"})
        required_tools = set(expected["required_tools"])
        require(required_tools <= TOOLS, f"{label}: unknown required tool")
        require("SearchKnowledge" in required_tools, f"{label}: a knowledge read is required")
        require(scenario["order_ref"] is None or "GetOrderStatus" in required_tools,
                f"{label}: the selected order must be read")
        require(not expected["ticket_created"] or "CreateSupportTicket" in required_tools,
                f"{label}: a created ticket requires its tool")
        require(not (set(expected["must_include"]) & set(expected["must_not_include"])),
                f"{label}: conflicting literal phrase expectations")
        require(not scenario["allow_ticket"] or expected["ticket_created"] or
                NO_TICKET_SENTENCE in expected["must_include"],
                f"{label}: include the stable no-new-ticket sentence")
        groups = expected["source_groups"]
        require(isinstance(groups, list), f"{label}: source_groups must be an array")
        for index, group in enumerate(groups, 1):
            group_label = f"{label} source group {index}"
            text_list(group, group_label, nonempty=True)
            require(set(group) <= known_documents,
                    f"{group_label}: unknown document IDs {sorted(set(group) - known_documents)}")
    require(categories == CATEGORIES,
            f"Missing scenario categories: {sorted(CATEGORIES - categories)}")


def validate_fixtures(path):
    fixtures = parse_json(path.read_text(encoding="utf-8-sig"))
    exact_fields(fixtures, ORDER_REFS, "Fixtures")
    for reference, order_id in fixtures.items():
        require(type(order_id) is int and 0 < order_id <= 2147483647,
                f"Fixtures: {reference} must be a positive C# int")
    require(fixtures["owned_order"] != fixtures["foreign_order"],
            "Fixtures: the owned and foreign order IDs must differ")
    return fixtures


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--scenarios", type=Path, default=ROOT / "eval/scenarios.jsonl")
    parser.add_argument("--knowledge-dir", type=Path, default=ROOT / "src/Support.API/Knowledge")
    parser.add_argument("--expected-count", type=int, default=10)
    parser.add_argument("--fixtures", type=Path,
                        help="Also check a local order-ID mapping; ownership is not checked offline")
    args = parser.parse_args()
    try:
        require(args.expected_count > 0, "expected-count must be positive")
        scenarios = load_scenarios(args.scenarios)
        validate_dataset(scenarios, document_ids(args.knowledge_dir), args.expected_count)
        if args.fixtures is not None:
            validate_fixtures(args.fixtures)
        setup_count = sum(len(scenario["setup"]) for scenario in scenarios)
        print(f"OK: {len(scenarios)} scenarios validated. No APIs or models were called.")
        print(f"One run in both modes: {2 * len(scenarios)} target requests + "
              f"{2 * setup_count} setup requests.")
        if args.fixtures is not None:
            print("Order ID format checked; existence and ownership still require verification.")
        else:
            print("Order fixtures were not checked.")
        return 0
    except (ContractError, OSError, UnicodeError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
