import json
import os
import unittest
from unittest.mock import patch
from relay_testkit import RelayHarness, BASE_PATH, DAV_ROOT

class PublicRoomTests(unittest.TestCase):
    def enroll(self, relay, device):
        result = relay.request('POST', BASE_PATH + '/v1/public-room/authorizations',
                               body=json.dumps({'deviceId': device}).encode())
        self.assertEqual(201, result.status)
        return result.json()

    def test_two_fresh_devices_share_room_with_distinct_revocable_grants(self):
        with RelayHarness() as relay:
            first = self.enroll(relay, 'phone-a')
            second = self.enroll(relay, 'phone-b')
            self.assertEqual(first['conversationId'], second['conversationId'])
            self.assertNotEqual(first['token'], second['token'])
            a = (first['conversationId'], first['token'])
            b = (second['conversationId'], second['token'])
            self.assertEqual(201, relay.request('PUT', DAV_ROOT+'hello.txt', basic=a, body=b'hello').status)
            self.assertEqual(b'hello', relay.request('GET', DAV_ROOT+'hello.txt', basic=b).body)
            relay.store.revoke_public_grant(first['grantId'])
            self.assertEqual(401, relay.request('GET', DAV_ROOT+'hello.txt', basic=a).status)
            self.assertEqual(200, relay.request('GET', DAV_ROOT+'hello.txt', basic=b).status)
            private = ('private-original', 'a'*64)
            self.assertEqual(201, relay.conversations(private).status)
            self.assertEqual(401, relay.request('GET', DAV_ROOT, basic=(private[0], second['token'])).status)

    def test_server_can_close_enrollment_and_invalid_device_is_rejected(self):
        with RelayHarness() as relay:
            with patch.dict(os.environ, {'MPT_RELAY_PUBLIC_ENROLLMENT':'closed'}):
                self.assertEqual(403, relay.request('POST', BASE_PATH+'/v1/public-room/authorizations', body=b'{"deviceId":"phone"}').status)
            self.assertEqual(400, relay.request('POST', BASE_PATH+'/v1/public-room/authorizations', body=b'{"deviceId":"../private"}').status)
