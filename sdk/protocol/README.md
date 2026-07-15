# MyPowerTools protocol bundle 0.2

This bundle is the language-neutral contract for external runtimes. It contains:

- `mpt_module_v1.proto`: module lifecycle, health, settings, commands, logs, and events.
- `mpt_host_control_v1.proto`: Runner/Shell catalog and refresh contract.
- `mpt_service_manager_v1.proto`: Service Unit lifecycle, scoped control, readiness endpoints, logs, and events.
- `tool.schema.json`: standalone tool manifest schema.
- `test-vectors`: valid request/response samples for cross-language implementations.

Protocol `1.x` keeps field numbers and existing enum meanings compatible. Consumers must ignore unknown fields. Producers must preserve request IDs and event sequence numbers. Scoped Service Unit clients use `UnitReadiness.kind/address/timeout` to connect to the instance-specific business endpoint returned by ServiceManager.
