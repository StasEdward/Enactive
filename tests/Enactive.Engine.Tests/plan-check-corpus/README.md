# Corpus of refused verification contracts

Each `*.json` here is a planner's verification contract that validation refused in a real run, stored
with exactly what it was checked against: the planner's answer, whether it arrived complete, the
request, the plan's checks, restrictions and action policy, the tool inventory, and whether the
criteria were locked.

`ARefusedPlanContractReplaysTests.Every_refusal_in_the_corpus_still_replays` replays every file
against today's validation (`PlanCheckContract.Validate`, the same function the review uses) and
holds the result to `expected.json`, exactly as the review corpus does - see its README for the rules.

## Where cases come from

A run writes every refused contract into the workspace it ran in:

    <workspace>/.enactive/plan-check-corpus/<time>-<hash>.json

The four seeded cases were rebuilt from the logs of runs ee6cf56a and f8e875bf (2026-09-28), which
held every input verbatim, before the recorder existed.

## What a case contains

The request text and model output. Look at a case before committing it.
