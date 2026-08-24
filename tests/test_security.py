"""Tests for server.security module."""

import sys
import os
import unittest

# Add server directory to path
sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', 'server'))

from security import validate_pin, PinAuth


class TestPinValidation(unittest.TestCase):
    """Test PIN format validation."""

    def test_valid_pin_numeric(self):
        self.assertTrue(validate_pin("1234"))

    def test_valid_pin_alphanumeric(self):
        self.assertTrue(validate_pin("abc12345"))

    def test_valid_pin_with_special_chars(self):
        self.assertTrue(validate_pin("a@b#c$"))

    def test_valid_pin_max_length(self):
        self.assertTrue(validate_pin("1234567890123456"))

    def test_valid_pin_min_length(self):
        self.assertTrue(validate_pin("1234"))

    def test_invalid_pin_too_short(self):
        self.assertFalse(validate_pin("123"))

    def test_invalid_pin_too_long(self):
        self.assertFalse(validate_pin("12345678901234567"))

    def test_invalid_pin_empty(self):
        self.assertFalse(validate_pin(""))

    def test_invalid_pin_not_string(self):
        self.assertFalse(validate_pin(None))
        self.assertFalse(validate_pin(1234))

    def test_invalid_pin_with_spaces(self):
        self.assertFalse(validate_pin("1234 5678"))

    def test_valid_pin_with_allowed_symbols(self):
        for ch in '!@#$%^&*()_-+=[]{}:;,.?/|~':
            self.assertTrue(validate_pin(f"123{ch}567"), f"Failed for char: {ch}")


class TestPinAuth(unittest.TestCase):
    """Test PinAuth token and lockout logic."""

    def setUp(self):
        self.auth = PinAuth()

    def test_validate_token_empty(self):
        self.assertFalse(self.auth.validate_token(""))

    def test_validate_token_none(self):
        self.assertFalse(self.auth.validate_token(None))

    def test_validate_token_invalid(self):
        self.assertFalse(self.auth.validate_token("nonexistent-token"))

    def test_check_locked_no_failures(self):
        self.assertIsNone(self.auth.check_locked("127.0.0.1"))

    def test_lockout_after_max_attempts(self):
        ip = "192.168.1.100"
        for _ in range(self.auth.max_attempts):
            self.auth.register_failure(ip)
        remaining = self.auth.check_locked(ip)
        self.assertIsNotNone(remaining)
        self.assertGreater(remaining, 0)

    def test_reset_failures_clears_lockout(self):
        ip = "192.168.1.101"
        for _ in range(self.auth.max_attempts):
            self.auth.register_failure(ip)
        self.auth.reset_failures(ip)
        self.assertIsNone(self.auth.check_locked(ip))


if __name__ == '__main__':
    unittest.main()