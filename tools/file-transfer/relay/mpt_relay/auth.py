"""Basic credential parsing and validation.

The public relay has exactly one credential pair and no user database:

* user name  = conversation id, the same 1-64 ``[A-Za-z0-9_-]`` segment
  ``AssistantValidation.ConversationId`` already enforces in the client,
* password   = conversation key, exactly 64 hex characters (the client accepts upper and
  lower case through ``Uri.IsHexDigit``, so both are accepted and folded to lower case
  before hashing).

Nothing in this module logs or returns the secret.
"""

from __future__ import annotations

import base64
import binascii
import re
from typing import NamedTuple, Optional

#: ``TransferFiles.DeviceId`` / ``AssistantValidation.ConversationId`` in the C# client.
CONVERSATION_ID_PATTERN = re.compile(r"^[A-Za-z0-9_-]{1,64}$")
#: ``Guid.NewGuid().ToString("N")`` is lower case; ``Uri.IsHexDigit`` accepts both cases.
CONVERSATION_KEY_PATTERN = re.compile(r"^[0-9a-fA-F]{64}$")

REALM = "mpt-relay"


class Credentials(NamedTuple):
    conversation_id: str
    conversation_key: str


class CredentialFormatError(ValueError):
    """The Authorization header carried a syntactically unusable pair."""


def parse_basic(header: Optional[str]) -> Optional[Credentials]:
    """Parses an ``Authorization: Basic`` header; returns ``None`` when it is absent/foreign."""
    if not header:
        return None
    parts = header.split(None, 1)
    if len(parts) != 2 or parts[0].lower() != "basic":
        return None
    token = parts[1].strip()
    try:
        decoded = base64.b64decode(token, validate=True)
    except (binascii.Error, ValueError):
        raise CredentialFormatError("Authorization 头不是合法的 Basic 凭据。")
    try:
        text = decoded.decode("utf-8")
    except UnicodeDecodeError as error:
        raise CredentialFormatError("Basic 凭据必须是 UTF-8。") from error
    user, separator, password = text.partition(":")
    if not separator:
        raise CredentialFormatError("Basic 凭据缺少冒号分隔。")
    return Credentials(user, password)


def validate(credentials: Credentials) -> Credentials:
    """Enforces the client's own field rules; raises :class:`CredentialFormatError`."""
    if not CONVERSATION_ID_PATTERN.match(credentials.conversation_id):
        raise CredentialFormatError("会话 id 必须是 1–64 位英文字母、数字、短横线或下划线。")
    if not CONVERSATION_KEY_PATTERN.match(credentials.conversation_key):
        raise CredentialFormatError("会话密钥必须是 64 位十六进制字符。")
    return Credentials(credentials.conversation_id, credentials.conversation_key.lower())


def encode_basic(conversation_id: str, conversation_key: str) -> str:
    """Client-side helper (used by the test suite) mirroring the C# header construction."""
    token = base64.b64encode(f"{conversation_id}:{conversation_key}".encode("utf-8")).decode("ascii")
    return f"Basic {token}"
