# Deploy to Azure

Deploys the AI Data Analyst as a public demo:

- **Azure SQL Database** (free tier) — holds the gold dataset
- **Azure Container Apps** — runs the API, built from the repo `Dockerfile`
- **OpenAI gpt-4o-mini** — generates the SQL (your API key)

Everything is scripted in [`deploy.ps1`](deploy.ps1). Run it **block by block** the first time.

## Prerequisites (do these once, in the browser / installer)

1. **Azure account.** Students can use [Azure for Students](https://azure.microsoft.com/free/students/) — $100 credit, **no credit card**. The SQL free tier + Container Apps free monthly grant keep this at ~$0.
2. **An LLM API key.** The app speaks the OpenAI-compatible protocol, so you can use a **free** provider — no credit card:
   - **Groq** (recommended, free): create a key at <https://console.groq.com/keys>. `deploy.ps1` defaults to it (`llama-3.3-70b-versatile`).
   - **Google Gemini** (free): <https://aistudio.google.com/apikey> — set `$LLM_BASEURL`/`$LLM_MODEL` accordingly.
   - **OpenAI** (paid, ~$5 min credit): leave `$LLM_BASEURL = ""` and use `gpt-4o-mini`.
3. **Tools** (PowerShell):
   ```powershell
   winget install Microsoft.AzureCLI
   winget install Docker.DockerDesktop   # if not already installed
   ```
   Then restart PowerShell and sign in:
   ```powershell
   az login
   ```

## Deploy

1. Open `deploy/azure/deploy.ps1`, edit the variables at the top — at minimum a globally-unique `$SQLSERVER` and your own `$SQLPASS`.
2. Put your OpenAI key in the shell (not in any file):
   ```powershell
   $env:OPENAI_KEY = "sk-...."
   ```
3. From the **repo root**, run the script's sections in order (resource group + SQL → seed → container app). On success it prints:
   ```
   Live demo:  https://ai-data-analyst.<region>.azurecontainerapps.io
   ```

## How it stays safe & cheap

- The app connects with the **`analyst_ro`** contained user — `SELECT` on `gold` only, writes/DDL denied (see `03_role.sql`). The SQL admin login is never used by the app.
- The OpenAI key is passed as a Container App **environment variable** from your shell — it is never committed to the repo.
- Free SQL tier auto-pauses when idle; Container Apps scales to zero. A demo typically costs nothing.

## Updating the app later

After code changes, just re-run the container step (rebuilds from the Dockerfile):
```powershell
az containerapp up --name ai-data-analyst --resource-group rg-analyst --source . --target-port 8080 --ingress external
```

## Troubleshooting

- **`--use-free-limit` errors** → your CLI/region may not support the flag. Create the database in the Azure Portal instead (choose *Serverless · General Purpose* and tick **Apply free offer**), then continue from the seed step.
- **Can't seed (login/firewall)** → make sure the `MyIP` firewall rule covers your current IP (the script adds it; re-run if your IP changed).
- **App starts but every query is refused / errors** → check the `ConnectionStrings__Analyst` env var on the Container App and that the seed step finished (`FactOrderItem rows = 12000`).
- **Tear everything down**: `az group delete -n rg-analyst --yes --no-wait`.
