# EnbNext.Protocol build boundary

This project contains only the small generic framing core used by the direct current-Net7 runtime:

- `Net/Packet.cs`
- `Net/PacketHeader.cs`

Historical Freya-server protocol sources were removed from this repository on 2026-08-20. They are not Net7 authority and are not retained under `src/` as archaeology. If historical comparison is ever needed, consult the upstream public Freya repository externally and treat it only as corroboration under the EnbNext evidence rules.

Do not re-vendor Freya opcode/record/codec sources into the active EnbNext tree. Current protocol behavior belongs in the evidence-backed `EnbNext.Net7` implementation.
