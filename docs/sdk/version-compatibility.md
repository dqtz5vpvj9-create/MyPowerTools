# Version compatibility

| Component | Current | Compatibility rule |
| --- | --- | --- |
| `tool.json` | 1.0 | Unknown optional fields are ignored; required identity and route fields stay stable in 1.x. |
| Module protocol | 1.0 | Existing protobuf field numbers and enum meanings remain stable in 1.x. |
| `MyPowerTools.ToolSdk` | 0.2.0 | Minor releases may add members; breaking source changes require the next major package. |
| `MyPowerTools.Protocol` | 0.2.0 | Generated contract follows module protocol 1.x. |
| `MyPowerTools.AvaloniaSdk` | 0.2.0 | Factory contract version is checked before surface creation. |
| `@mypowertools/web-bridge` | 0.2.0 | Promise methods and event names remain compatible within 0.2.x. |

Tool manifests should declare the lowest SDK version they consume once a release channel is established. Protocol clients must ignore unknown fields.
