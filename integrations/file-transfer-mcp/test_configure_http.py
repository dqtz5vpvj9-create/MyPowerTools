import unittest
import tomllib
from configure_http import migrate_config


class ConfigTests(unittest.TestCase):
    def test_preserves_other_servers_and_replaces_all_old_aliases(self):
        text = '''model = "test"
[mcp_servers.other]
command = "other-command"
[mcp_servers.mpt-file-transfer]
command = "old-python"
enabled = false
disabled_tools = ["mpt_cancel"]
[mcp_servers.mpt-file-transfer.env]
MPT_DATA_ROOT = "root"
[mcp_servers.mypowertools-file-transfer]
command = "second-python"
[projects."/path"]
trust_level = "trusted"
'''
        result, name = migrate_config(text, 'http://127.0.0.1:17843/mcp', 'example-secret')
        config = tomllib.loads(result)
        self.assertEqual(name, 'mpt-file-transfer')
        self.assertEqual(config['model'], 'test')
        self.assertEqual(config['projects']['/path']['trust_level'], 'trusted')
        self.assertEqual(config['mcp_servers']['other']['command'], 'other-command')
        self.assertEqual(len(config['mcp_servers']), 2)
        self.assertNotIn('command', config['mcp_servers'][name])
        self.assertNotIn('env', config['mcp_servers'][name])
        self.assertFalse(config['mcp_servers'][name]['enabled'])
        self.assertEqual(config['mcp_servers'][name]['disabled_tools'], ['mpt_cancel'])
        again, _ = migrate_config(result, 'http://127.0.0.1:17843/mcp', 'example-secret')
        self.assertEqual(result, again)

    def test_quoted_table_name_and_empty_config(self):
        for text in ['', '[mcp_servers."mpt-file-transfer"]\ncommand="old"\n']:
            result, name = migrate_config(text, 'http://127.0.0.1:17843/mcp', 'example-secret')
            self.assertEqual(tomllib.loads(result)['mcp_servers'][name]['http_headers']['Authorization'], 'Bearer example-secret')


if __name__ == '__main__':
    unittest.main()
