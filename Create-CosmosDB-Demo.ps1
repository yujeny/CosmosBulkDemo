# ----- setting -----
$RG = 'rg-cosmos-demo'
$LOC = 'koreacentral'         # 한국은 'koreacentral'
$ACC = "cosmos-demo-$([System.Random]::new().Next(10000,99999))"
$DB  = 'demoDB'
$CTR = 'orders'
$PK  = '/userId'
$THROUGHPUT = 4000         # Autoscale limits RU/s

# ----- Check Azure login -----
az account show --only-show-errors 1>$null 2>$null
if ($LASTEXITCODE -ne 0) {
  Write-Output 'Not logged in. Opening Azure sign-in...'
  az login | Out-Null
}

# ----- Register Resource provider (Microsoft.DocumentDB) -----
$rp = az provider show -n Microsoft.DocumentDB --query 'registrationState' -o tsv 2>$null
if ($rp -ne 'Registered') {
  Write-Output 'Registering resource provider Microsoft.DocumentDB...'
  az provider register --namespace Microsoft.DocumentDB | Out-Null
  do {
    Start-Sleep -Seconds 3
    $rp = az provider show -n Microsoft.DocumentDB --query 'registrationState' -o tsv 2>$null
    Write-Output ("  state: {0}" -f $rp)
  } while ($rp -ne 'Registered')
}

# ----- Resource Group -----
az group create -n $RG -l $LOC | Out-Null

# ----- Cosmos Account (Core/SQL) -----
az cosmosdb create `
  -g $RG -n $ACC `
  --locations regionName=$LOC failoverPriority=0 isZoneRedundant=false `
  --default-consistency-level Session | Out-Null

# ----- database -----
az cosmosdb sql database create -g $RG -a $ACC -n $DB | Out-Null

# ----- container -----
az cosmosdb sql container create `
  -g $RG -a $ACC -d $DB -n $CTR `
  --partition-key-path $PK `
  --max-throughput $THROUGHPUT | Out-Null

# ----- endpiont -----
$ENDPOINT = az cosmosdb show -g $RG -n $ACC --query 'documentEndpoint' -o tsv
$KEY      = az cosmosdb keys list -g $RG -n $ACC --type keys --query 'primaryMasterKey' -o tsv

Write-Output ('ResourceGroup : {0}' -f $RG)
Write-Output ('AccountName   : {0}' -f $ACC)
Write-Output ('Database      : {0}' -f $DB)
Write-Output ('Container     : {0}' -f $CTR)
Write-Output ('Region        : {0}' -f $LOC)
Write-Output ('COSMOS_ENDPOINT = {0}' -f $ENDPOINT)
Write-Output ('COSMOS_KEY      = {0}' -f $KEY)
