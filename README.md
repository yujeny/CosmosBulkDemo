# 1. Local PC에 .NET SDK를 설치
dotnet --version
으로 확인

```bash
mkdir -p CosmosBulkDemo/src/Models 
mkdir -p CosmosBulkDemo/.vscode
cd CosmosBulkDemo/src
dotnet new console -n CosmosBulkDemo
dotnet add package Microsoft.Azure.Cosmos --version 3.54.0
dotnet add package Newtonsoft.Json --version 13.0.3
dotnet restore
dotnet rerun
으로 정상적으로 되는지 확인
```

# 2. CosmosDB 생성
```bash
az login
az account set --subscription "<구독 이름 또는 ID>"
Create-CosmosDB-Demo.ps1 수행
```
여기에서 나온 COSMOS_ENDPOINT값과 COSMOS_KEY값을 appsettings.json 또는 launch.json 에 붙여넣음

만약 실행 policy 오류가 뜨면
``` bash
Set-ExecutionPolicy -Scope CurrentUser -ExecutionPolicy RemoteSigned
```
후 실행.


# 3. program run
``` bash
cd CosmosBulkDemo/src
dotnet clean
dotnet build
dotnet run
```


이 이후 생성된 데이터는 Portal -> CosmosDB 걔정 선택 -> Data Explorer -> "demoDB" -> "orders" container
파티션 키 /userID 확인

TTL 설정을 보기 위해서는 Container -> Settings-> 옆에서 Scale & Settings를 선택하고 Scale 옆 Settings를 선택하면 "Time to Live"가 보임

```bash
# TTL Monitoring
  az monitor metrics list `
  --resource "/subscriptions/<Subscription ID>/resourceGroups/<resource group name>/providers/Microsoft.DocumentDB/databaseAccounts/<cosmod DB account name>" `
  --metric "TotalRequestUnits, ThrottledRequests, StorageUsed" `
  --interval PT1H `
  --output table
  ```
