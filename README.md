
## Running

```shell
mkdir pulumi-state
cd infra
pulumi login file://../pulumi-state
```

```shell
pulumi config set --secret HONEYCOMB_API_KEY <your api key>
pulumi config set azure-native:location <location e.g. uksouth>
```