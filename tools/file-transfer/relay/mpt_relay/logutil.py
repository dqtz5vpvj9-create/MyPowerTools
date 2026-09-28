"""Log helpers.

The public relay must never write a credential to a log. Two things are protected:

* the ``Authorization`` header and every request body are never logged at all,
* conversation ids are masked by default, because a conversation id together with its key
  is a credential and the id appears in WebDAV paths.

``MPT_RELAY_MASK_LOG_IDS=0`` restores full ids for local debugging only.
"""

from __future__ import annotations

import re
from typing import Optional

_ASSISTANT_ID = re.compile(r"(assistant/)([^/]+)")


def mask_id(value: str) -> str:
    if len(value) <= 8:
        return "…"
    return f"{value[:4]}…{value[-4:]}"


def mask_path(path: str, mask: bool = True) -> str:
    """Masks the conversation id segment of a WebDAV path."""
    if not mask:
        return path
    return _ASSISTANT_ID.sub(lambda match: match.group(1) + mask_id(match.group(2)), path)


def mask_identity(conversation_id: str, mask: bool = True) -> str:
    return mask_id(conversation_id) if mask else conversation_id


def short_reason(error: Optional[BaseException]) -> str:
    if error is None:
        return ""
    return type(error).__name__
