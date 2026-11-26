"""MyPowerTools public file-transfer relay.

A Python 3 standard-library-only resident service that gives the file assistant a public
transport without Tailscale, a user account or any cloud drive:

* ``/mpt/relay/v1/conversations`` - first-use namespace registration (Basic auth).
* ``/mpt/relay/v1/changes``       - authenticated long poll (at most 25 seconds).
* ``/mpt/relay/dav/``             - WebDAV, one private root per conversation, compatible
  with ``FileTransfer.Core.OpenListClient`` / ``OpenListAssistantClient``.
* ``/mpt/relay/health``           - short unauthenticated liveness JSON.

The service listens on loopback only; nginx terminates TLS and forwards the prefix
unchanged. Nothing here writes to stdout in a way that could contain a conversation key.
"""

__version__ = "1.0.0"
__all__ = ["__version__"]
