# Hello world

The smallest useful Autobahn test: one scenario, a ramp, a hold.

```bash
dotnet run --project examples/HelloWorld
```

It writes its reports under `reports/` and prints the summary table on the way out.
Pass `--config` or `--target` through and they reach the runner:

```bash
dotnet run --project examples/HelloWorld -- --target hello_world_scenario
```
