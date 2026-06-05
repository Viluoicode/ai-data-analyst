# =============================================================================
#  Deploy AI Data Analyst to Azure
#  - Azure SQL Database (free tier)        -> the gold data
#  - Azure Container Apps (from Dockerfile) -> the API
#  - LLM: OpenAI gpt-4o-mini (your key)
#
#  Prereqs: Azure CLI + Docker installed; `az login` done; an OpenAI API key.
#  Run from the REPO ROOT. Edit the variables, then run blocks step by step
#  (don't blindly run the whole file the first time).
# =============================================================================

# ---- 1. Variables (EDIT THESE) ---------------------------------------------
$RG        = "rg-analyst"
$LOCATION  = "southeastasia"
$SQLSERVER = "analyst-sql-CHANGE-ME"        # must be globally unique, lowercase
$SQLADMIN  = "sqladmin"
$SQLPASS   = "ChangeMe_Strong#2026"         # SQL admin password (you choose)
$DB        = "AnalystDB"
$APP       = "ai-data-analyst"
$ROPASS    = "Readonly#Analyst1"            # read-only app user pwd (must match 03_role.sql)

# LLM provider (OpenAI-compatible). Defaults to Groq's FREE API (no credit card).
#   Groq    : BaseUrl https://api.groq.com/openai/v1   Model llama-3.3-70b-versatile
#   Gemini  : BaseUrl https://generativelanguage.googleapis.com/v1beta/openai/  Model gemini-2.0-flash
#   OpenAI  : BaseUrl ""  (leave empty)                 Model gpt-4o-mini   (needs paid credit)
$LLM_BASEURL = "https://api.groq.com/openai/v1"
$LLM_MODEL   = "llama-3.3-70b-versatile"

# API key — set it in your shell, do NOT hardcode it here:
#   $env:OPENAI_KEY = "<your provider key>"
if (-not $env:OPENAI_KEY) { Write-Warning "Set `$env:OPENAI_KEY before the container deploy step." }

# ---- 2. Resource group + Azure SQL -----------------------------------------
az group create -n $RG -l $LOCATION

az sql server create -g $RG -n $SQLSERVER -l $LOCATION -u $SQLADMIN -p $SQLPASS

# Allow other Azure services (the Container App) to reach the SQL server:
az sql server firewall-rule create -g $RG -s $SQLSERVER -n AllowAzure `
  --start-ip-address 0.0.0.0 --end-ip-address 0.0.0.0

# Allow YOUR current IP so you can seed the database:
$myip = (Invoke-RestMethod https://api.ipify.org)
az sql server firewall-rule create -g $RG -s $SQLSERVER -n MyIP `
  --start-ip-address $myip --end-ip-address $myip

# Free-tier database (Serverless, General Purpose). If these flags error on your
# CLI/region, create the DB in the Portal with "Apply free offer" instead.
az sql db create -g $RG -s $SQLSERVER -n $DB `
  --edition GeneralPurpose --compute-model Serverless --family Gen5 --capacity 2 `
  --use-free-limit --free-limit-exhaustion-behavior AutoPause

# ---- 3. Seed the database (uses a throwaway sqlcmd container — no install) --
$server = "$SQLSERVER.database.windows.net"
$img    = "mcr.microsoft.com/mssql/server:2022-latest"
foreach ($f in @("01_schema.sql","02_seed.sql","03_role.sql")) {
  docker run --rm -v "${PWD}/deploy/azure:/scripts" $img `
    /opt/mssql-tools18/bin/sqlcmd -S $server -U $SQLADMIN -P $SQLPASS -d $DB -C -b -i "/scripts/$f"
}

# ---- 4. Deploy the API to Azure Container Apps (builds from the Dockerfile) -
az extension add --name containerapp --upgrade
az provider register -n Microsoft.App --wait
az provider register -n Microsoft.OperationalInsights --wait

$conn = "Server=tcp:$server,1433;Database=$DB;User ID=analyst_ro;Password=$ROPASS;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"

az containerapp up `
  --name $APP `
  --resource-group $RG `
  --location $LOCATION `
  --source . `
  --ingress external `
  --target-port 8080 `
  --env-vars `
     "ConnectionStrings__Analyst=$conn" `
     "Analyst__Provider=OpenAI" `
     "Analyst__OpenAI__BaseUrl=$LLM_BASEURL" `
     "Analyst__OpenAI__Model=$LLM_MODEL" `
     "Analyst__OpenAI__ApiKey=$env:OPENAI_KEY"

# ---- 5. Show the public URL -------------------------------------------------
$fqdn = az containerapp show -g $RG -n $APP --query "properties.configuration.ingress.fqdn" -o tsv
Write-Host "`nLive demo:  https://$fqdn`n" -ForegroundColor Green
