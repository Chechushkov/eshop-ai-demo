# eShop Reference Application - "AdventureWorks"

A reference .NET application implementing an e-commerce website using a services-based architecture with [Aspire](https://aspire.dev/).

## What this fork adds

[![Support self-tests](https://github.com/Chechushkov/eshop-ai-demo/actions/workflows/support-selftests.yml/badge.svg?branch=tutorial%2Fagents&event=push)](https://github.com/Chechushkov/eshop-ai-demo/actions/workflows/support-selftests.yml)

An educational AI customer-support service integrated into dotnet/eShop:

- RAG over eight demo-policy documents stored in PostgreSQL with pgvector.
- A custom C# agent loop and a Microsoft Agent Framework workflow graph.
- Tools for knowledge search, order status and support-ticket creation, with
  permission and order-ownership checks enforced in C#.
- Persistent conversations and replay of saved requests by identifier.
- Execution traces, per-message timing and token metrics, and 23 offline checks
  automated in GitHub Actions.

Start with [Support.API](src/Support.API), the
[support page](src/WebApp/Components/Pages/Support.razor) and the
[offline checks](tests/Support.SelfTests).
The [initial support implementation](https://github.com/Chechushkov/eshop-ai-demo/commit/6003348)
shows the original changes added to eShop.

Read the [support architecture](docs/architecture.md) and the decisions on
[tool authority](docs/adr/0001-model-proposes-code-decides.md),
[agent and workflow orchestration](docs/adr/0002-custom-agent-loop-and-framework-workflow.md),
and [model integration](docs/adr/0003-direct-responses-api.md).

Engineering comments, diagnostics, test output and documentation use English.
See the [language conventions](CONTRIBUTING.md#language-conventions-for-this-fork);
the support page also uses English labels and controls. Stored conversation content
retains its original language.

### Run the support demo

Complete the [prerequisites](#prerequisites), including cloning this repository.
Run the following commands from the repository root on the development machine
that hosts the AppHost. The configuration block uses Bash and prompts for the
API key without adding its literal value to the command history:

```bash
bash <<'BASH'
set -e
read -r -s -p "OpenAI API key: " eshop_support_key </dev/tty
printf '\n'
dotnet user-secrets set "Parameters:support-openai-key" "$eshop_support_key" --project src/eShop.AppHost
unset eshop_support_key
dotnet user-secrets set "Support:Model" "gpt-5.4-mini" --project src/eShop.AppHost
BASH
```

The AppHost stores development configuration using .NET user secrets and passes
the API key to Support.API through a secret Aspire parameter. User secrets are
stored outside the repository; they are not encrypted and are intended for
development. See the [.NET user-secrets documentation](https://learn.microsoft.com/aspnet/core/security/app-secrets?view=aspnetcore-10.0).

Support.API calls the OpenAI Responses API directly. Its default chat model is
`gpt-5.4-mini`; embeddings use `text-embedding-3-small` with 1536 dimensions.
The upstream Foundry chatbot is a separate feature.

With the container runtime running, start the application:

```bash
ESHOP_USE_HTTP_ENDPOINTS=1 aspire run
```

The first startup imports the demo documents and generates their embeddings.
Unchanged documents reuse the stored embeddings on subsequent starts.

Open <http://localhost:5045/support> and sign in through eShop. The support page
uses English labels and controls. The current assistant prompts and knowledge
documents still use Russian; saved conversation content retains its original
language. Try a general question without selecting an order or enabling ticket
creation:

> Можно вернуть кофемолку, если я открыл коробку, но не пользовался?

Then ask a follow-up in the same conversation:

> А когда вернут деньги?

For the order demo, create an order through the storefront, select it on the
support page and ask to check its status. Enable ticket creation when you want
to save a demo support ticket. Inspect the sources, execution trace and metrics
shown with the reply. Use "Repeat last request" to replay the saved reply.

For development on a remote server, forward the browser-facing ports
`5045` (WebApp), `5223` (Identity.API) and `18848` (Aspire dashboard) through SSH
or your IDE. Open the application through `localhost` on your own computer.
The current HTTP and callback settings are for local or tunneled development.

### Current behavior and verification scope

- For a valid order owned by the signed-in buyer, enabling `AllowTicket`
  currently requests ticket creation in both modes. The agent completes this
  action in C# if the model omits it. Model-based ticket-necessity decisions
  are planned.
- Tickets are stored in `support_tickets` in the `knowledge` database and do not
  contact an external support service. Store policies are fictional demo rules.
- Repeating a saved request with the same `conversationId`, `requestId` and
  payload returns its saved reply. Reusing that identifier with different
  parameters returns HTTP 409. Matching question text alone is not a replay.
- Reply validation checks retrieved source IDs and known ticket numbers.
  Invalid drafts get one repair attempt before a deterministic fallback.
- The 23 offline checks use fake dependencies. They verify selected code paths;
  real-model quality and PostgreSQL integration require separate evaluation.

Run the support checks without an API key or a running application:

```bash
dotnet run --project tests/Support.SelfTests/Support.SelfTests.csproj --configuration Release
```

Expected final output:

```text
OK: 23 offline checks passed. No external APIs or PostgreSQL were called.
```

![eShop Reference Application architecture diagram](img/eshop_architecture.png)

![eShop homepage screenshot](img/eshop_homepage.png)

## Getting Started

This version of eShop is based on .NET 10.

Previous eShop versions:

* [.NET 8](https://github.com/dotnet/eShop/tree/release/8.0)

### Prerequisites

1. Install a [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) that satisfies [`global.json`](global.json).
2. Install the [Aspire CLI](https://aspire.dev/get-started/install-cli/) and verify that it is available:

    ```console
    aspire --version
    ```

3. Install and start an OCI-compatible container runtime. [Docker Desktop](https://www.docker.com/products/docker-desktop/) is the recommended default. [Podman](https://podman.io/docs/installation) is also supported; follow the [Aspire prerequisites](https://aspire.dev/get-started/prerequisites/) to configure it.
4. Clone the repository:

    ```console
    git clone https://github.com/dotnet/eShop.git
    cd eShop
    ```

No separate Aspire workload or Visual Studio component is required; the AppHost SDK and hosting integrations are referenced by the projects in this repository.

#### Optional IDE setup

- [Visual Studio](https://visualstudio.microsoft.com/vs/) with the `ASP.NET and web development` workload.
- [Visual Studio Code with C# Dev Kit](https://code.visualstudio.com/docs/csharp/get-started) and the [Aspire extension](https://aspire.dev/get-started/aspire-vscode-extension/).
- The [.NET MAUI workload](https://learn.microsoft.com/dotnet/maui/get-started/installation) if you want to run the client apps.

### Running the solution

> [!WARNING]
> Ensure that your container runtime is running before starting eShop.

#### From the terminal

From the repository root, run:

```console
aspire run
```

The root [`aspire.config.json`](aspire.config.json) selects `src/eShop.AppHost/eShop.AppHost.csproj`, avoiding ambiguity with the test AppHosts in the repository. When startup completes, the CLI prints a dashboard URL similar to:

```text
Dashboard: https://localhost:<port>/login?t=<token>
```

Press <kbd>Ctrl</kbd>+<kbd>C</kbd> to stop the AppHost. See the [`aspire run` command](https://aspire.dev/reference/cli/commands/aspire-run/) for additional options.

To run the AppHost in the background instead:

```console
aspire start
aspire ps
```

When you are finished, run `aspire stop`. See the [`aspire start` command](https://aspire.dev/reference/cli/commands/aspire-start/) for details.

#### From Visual Studio

1. Open `eShop.Web.slnf`.
2. Set `src/eShop.AppHost/eShop.AppHost.csproj` as the startup project.
3. Press <kbd>Ctrl</kbd>+<kbd>F5</kbd> to start eShop and open the Aspire dashboard.

### Running tests

Run the server tests:

```powershell
dotnet test --solution eShop.Web.slnf
```

Run the Playwright browser journeys. Playwright starts the AppHost automatically, so ensure your container runtime is running first.

```powershell
npm ci
npx playwright install chromium
npm run test:e2e
```

### Optional: AI Chatbot with Microsoft Foundry

This option provisions a Microsoft Foundry resource during local development, so first authenticate to Azure and configure the subscription and location:

```powershell
az login
aspire secret set "Azure:SubscriptionId" "<subscription-id>"
aspire secret set "Azure:Location" "eastus"
```

Then enable Foundry and start eShop:

```powershell
$env:UseFoundry = "true"
aspire run
```

Aspire provisions the `gpt-4.1-mini` and `text-embedding-3-small` deployments and injects their connection information into the consuming projects. The Foundry hosting integration currently uses a preview package. See [local Azure provisioning](https://aspire.dev/integrations/cloud/azure/local-provisioning/) and the [Microsoft Foundry hosting integration](https://aspire.dev/integrations/cloud/azure/azure-ai-foundry/azure-ai-foundry-host/) for details.

### Deploy to Azure Container Apps

The AppHost is already configured with an Azure Container Apps environment, so the Aspire CLI can deploy directly from the application model. See the [Aspire Azure Container Apps deployment guide](https://aspire.dev/deployment/azure/container-apps/) for details.

> [!WARNING]
> This sample deploys PostgreSQL, Redis, and RabbitMQ as containers in Azure Container Apps. This configuration is intended for evaluation and demonstrations, not production data.

Prerequisites:

- The prerequisites listed above, including a running container runtime.
- The [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli), an active Azure subscription, and permission to create resources.

Sign in, optionally preview the deployment pipeline, and deploy:

```console
az login
aspire deploy --list-steps
aspire deploy
```

For local interactive use, `aspire deploy` prompts for missing Azure settings. For non-interactive use, provide them explicitly:

```powershell
$env:Azure__SubscriptionId = "<subscription-id>"
$env:Azure__Location = "eastus"
$env:Azure__ResourceGroup = "rg-eshop-demo"
aspire deploy --non-interactive
```

Use [`aspire publish`](https://aspire.dev/reference/cli/commands/aspire-publish/) when you need deployment artifacts for inspection or another deployment tool. Running it first is not required: `aspire deploy` invokes the deployment pipeline and its dependencies directly rather than consuming an earlier publish output.

When you no longer need the deployment, run [`aspire destroy`](https://aspire.dev/reference/cli/commands/aspire-destroy/). This deletes the entire configured resource group, including resources that Aspire did not create, so review the target carefully before confirming.

## Contributing

For more information on contributing to this repo, read [the contribution documentation](./CONTRIBUTING.md) and [the Code of Conduct](CODE-OF-CONDUCT.md).

### Sample data

The sample catalog data is defined in [catalog.json](https://github.com/dotnet/eShop/blob/main/src/Catalog.API/Setup/catalog.json). Those product names, descriptions, and brand names are fictional and were generated using [GPT-35-Turbo](https://learn.microsoft.com/en-us/azure/ai-services/openai/how-to/chatgpt), and the corresponding [product images](https://github.com/dotnet/eShop/tree/main/src/Catalog.API/Pics) were generated using [DALL·E 3](https://openai.com/dall-e-3).

## eShop on Azure

For a version of this app configured for deployment on Azure, please view [the eShop on Azure](https://github.com/Azure-Samples/eShopOnAzure) repo.
