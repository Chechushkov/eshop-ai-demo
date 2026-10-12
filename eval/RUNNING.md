# Run agent and workflow evaluation

Run the .NET commands from the repository root on the machine hosting eShop.
The C# console project uses the .NET 10 base class library. It defaults to a local plan;
`--execute` and an explicit estimated budget are required for model requests.

## Authentication and trust boundary

Support.API needs an **eShop buyer access token** with the `orders` audience. The
OpenAI key stays in the backend's existing configuration. An API key does not
identify a buyer and cannot replace the buyer token.

Identity.API registers `support-eval` only when its host environment is
Development. It is a public authorization-code client with PKCE, the `orders`
scope and the exact callback `http://127.0.0.1:8765/callback`. It does not use a
shared client secret, accept passwords through the runner, or issue refresh
tokens. Access tokens last one hour; normal API authentication and ownership
checks remain in force.

The `login` command opens or prints an authorization URL. A buyer logs in through the
existing Identity.API page. The temporary callback listener verifies state and
exchanges the authorization code using its private PKCE verifier. The token is
written with mode `0600` and is not printed or copied into evaluation results.

With Cursor Remote SSH, start the helper on the VDS using `--no-browser`, then
forward port `8765` through Cursor with the same local port. Open the printed URL
on the Mac. Keep the existing Identity.API tunnel on `5223`. Both the helper and
the browser must reach the same Identity.API instance.

Example on the VDS:

```bash
dotnet run --project tools/Support.Evaluation -- login --no-browser \
  --token-file /home/dev/.config/eshop-dev/eval-buyer-token.json
```

Repeat with a different test buyer for the foreign-order token:

```bash
dotnet run --project tools/Support.Evaluation -- login --no-browser \
  --token-file /home/dev/.config/eshop-dev/eval-foreign-token.json
```

The helper forces a new login prompt for each invocation. Choose the account
that owns the relevant fixture order. After a denied or expired login, start a
new helper invocation and use its new URL.

Alternatively run the `login` command on the Mac with its own local listener and privately
transfer the resulting token files to the VDS.

References: [Duende client settings](https://docs.duendesoftware.com/identityserver/reference/v7/models/client/)
and [OAuth for native applications](https://www.rfc-editor.org/rfc/rfc8252.html).

## Local plan and authenticated preflight

Fill `eval/fixtures.local.json` with the existing owned and foreign order IDs.
First print the plan without making any HTTP request:

```bash
dotnet run --project tools/Support.Evaluation -- plan
```

The full dataset requires 20 target requests plus 2 history setup requests.
`--max-requests` limits the total, including setup. Repeats require a larger
explicit request limit.

Then check authentication and ownership without asking the model a question:

```bash
dotnet run --project tools/Support.Evaluation -- run --preflight-only \
  --buyer-token-file /home/dev/.config/eshop-dev/eval-buyer-token.json \
  --foreign-token-file /home/dev/.config/eshop-dev/eval-foreign-token.json
```

Preflight reads the buyer-filtered order list under **each buyer's token**. It
verifies that each fixture exists for the intended buyer and is absent from the
other buyer's list. It also checks that Support.API accepts the main token.
It does not call the single-order endpoint, generate embeddings or ask the model.
This is stronger than treating an arbitrary nonexistent ID as a foreign order.

Default API URLs are `http://localhost:5260` for Support.API and
`http://localhost:5224` for Ordering.API, matching the source launch profiles.
If your Aspire endpoints differ, provide `--support-url` and `--ordering-url`
from the dashboard. Run on the VDS; these are loopback addresses there. The
HTTP clients bypass system proxies for this local traffic and do not follow
redirects with the bearer token.

## First four target requests

Start with two scenarios in both modes: a return policy and owned-order status.
Neither is expected to create a ticket.

```bash
dotnet run --project tools/Support.Evaluation -- run --execute \
  --only policy-opened-box,owned-order-status \
  --budget-usd 1.00 \
  --buyer-token-file /home/dev/.config/eshop-dev/eval-buyer-token.json \
  --foreign-token-file /home/dev/.config/eshop-dev/eval-foreign-token.json
```

Only after inspecting those results, run all ten cases once in each mode:

```bash
dotnet run --project tools/Support.Evaluation -- run --execute --budget-usd 1.00 \
  --buyer-token-file /home/dev/.config/eshop-dev/eval-buyer-token.json \
  --foreign-token-file /home/dev/.config/eshop-dev/eval-foreign-token.json
```

Each run creates fresh conversation and request identifiers. Repeating a run is
a new measurement and intentionally permits new demo tickets. Four saved tickets
are expected for a completely successful full pass. `DemoRetry` is always false.
The runner never retries a lost or failed response automatically: the server may
already have saved a ticket. The original request parameters are recorded before
POST for investigation or an intentional replay with the same identifiers.

## Estimated budget and token usage

`prices.json` records the model names, source URLs, verification date and standard
prices per million tokens. On 2026-10-10, the model pages list:

| Model | Input USD / 1M | Output USD / 1M |
| --- | ---: | ---: |
| `gpt-5.4-mini` | 0.75 | 4.50 |
| `text-embedding-3-small` | 0.02 | Not applicable |

Sources: [GPT-5.4 Mini](https://developers.openai.com/api/docs/models/gpt-5.4-mini)
and [text-embedding-3-small](https://developers.openai.com/api/docs/models/text-embedding-3-small).
These are a dated snapshot; check rates before future runs. For another backend
model or pricing arrangement, provide a matching file using `--prices`.

The estimate is `(input tokens × input rate + output tokens × output rate +
embedding tokens × embedding rate) / 1,000,000`. It includes setup turns and all
model calls counted by the backend, including query rewriting, tool selection,
classification and repair. Cached-input counts are not separated by the current
backend, so all input uses the uncached rate. This can overestimate discounted
input cost. It is not an invoice and excludes startup indexing, unrelated
application requests, taxes and account-specific pricing.

The default headroom is `0.10` USD per next request, configurable with
`--request-headroom-usd`. The runner checks remaining estimated headroom before
POST and updates cost after receiving usage. It stops if usage is unknown, a
reply is replayed, model names differ from the price file, the request cap is
reached, or the estimated budget is exhausted.

**This is a client-side stop between requests, not a provider spending cap.** An
in-flight request can cost more than the headroom and exceed the selected budget.
Unknown usage causes an immediate stop rather than being charged as zero. The
budget is per invocation; it does not enforce a monthly or account-wide limit.

## Results, database checks and manual review

The runner prints its directory under `eval/results/`. It contains:

| File | Purpose |
| --- | --- |
| `manifest.json` | Commit, dirty-tree flag, dataset hash, fixtures, prices, modes and request plan |
| `requests.jsonl` | Parameters saved before each paid POST; no authorization headers |
| `responses.jsonl` | Replies, sources, traces, observations, usage, elapsed time and automatic checks |
| `run_status.json` | Attempted requests, known cost subtotal and any stop reason |
| `verify_tickets.sql` | Read-only row-count checks for every attempted request, including denied writes |
| `reviews.example.jsonl` | Target request IDs with semantic and database checks initially unknown |
| `summary.json`, `summary.md` | Quality states, intervention counts, latency and usage |

The default result directory and the local token/fixture files used here are excluded from Git.
The other buyer's token is used only for preflight, never for the target support
requests.

Execute `verify_tickets.sql` in the **knowledge** database using your existing
Aspire PostgreSQL connection. Each expected write should have exactly one row;
every other request should have zero. Check returned ticket IDs, order IDs and
buyer IDs as well as the row count. This can reveal an unexpected write even if
the reply says no ticket was created. A trace entry alone cannot prove a write.

Copy `reviews.example.jsonl` to `reviews.jsonl` inside the run directory. For each
target, read the response and its expected facts/forbidden claims in
`responses.jsonl`, including any preceding setup turn. Record:

```json
{"request_id":"REPLACE_WITH_TARGET_UUID","semantics_passed":true,"ticket_rows_verified":true,"notes":"Answer and persisted ticket rows match the expected behavior."}
```

Use false for a failed check, and null while its result is unknown. Literal
forbidden-phrase matches are flags for reviewing the complete sentence; they do
not replace semantic assessment.

Rebuild the report after filling the review file:

```bash
dotnet run --project tools/Support.Evaluation -- summarize --run-dir eval/results/REPLACE_WITH_RUN_DIRECTORY \
  --reviews eval/results/REPLACE_WITH_RUN_DIRECTORY/reviews.jsonl
```

Until the automatic, semantic and database checks pass, an automatic pass remains
`pending_review`. Failed required checks produce `unsolved`; verified successes
are `solved` or `solved_via_fallback` according to the application flag. Failed
history preparation produces `invalid_setup`, separately from evaluated targets.
Missing behavior remains unknown. Read completion and recomposition are reported
independently of quality.

The median and nearest-rank p95 are descriptive pilot statistics. A first pass
with ten questions does not establish stable quality or reliable tail latency.
The next stage is repeated runs on a larger dataset, after reviewing this pilot.

## Offline verification

```bash
dotnet run --project tools/Support.Evaluation -- validate
dotnet test --project tests/Support.Evaluation.UnitTests
```

The MSTest unit tests use in-memory dependencies, HTTP handlers and temporary
files. They do not call eShop, OpenAI or PostgreSQL. The evaluation CI workflow also builds
Identity.API, but never starts login or the paid runner. Both projects are included
in `eShop.slnx` and `eShop.Web.slnf`. The MSTest SDK version comes from the existing
`global.json`; no new test framework or Python installation is needed. The
existing C# Support self-tests remain a separate verification suite.
