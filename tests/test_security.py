import sys
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "server"))

from security import PinAuth, load_config, save_config, validate_pin  # noqa: E402


class SecurityTests(unittest.TestCase):
    def setUp(self):
        config = load_config()
        self._saved_config = config.copy()

    def tearDown(self):
        save_config(self._saved_config)

    def test_pin_policy_accepts_expected_values(self):
        self.assertTrue(validate_pin("Abc1"))
        self.assertTrue(validate_pin("Abc@1234"))
        self.assertTrue(validate_pin("A1!@#$%^&*()_+-"))

    def test_pin_policy_rejects_invalid_values(self):
        self.assertFalse(validate_pin("abc"))
        self.assertFalse(validate_pin("a" * 17))
        self.assertFalse(validate_pin("中文1234"))
        self.assertFalse(validate_pin("abc 1234"))

    def test_pin_set_and_login(self):
        auth = PinAuth()
        self.assertTrue(auth.set_pin("MyPIN123"))
        self.assertTrue(auth.is_configured())
        token = auth.login("MyPIN123")
        self.assertIsNotNone(token)
        self.assertIsNone(auth.login("WrongPin1"))

    def test_pin_change(self):
        auth = PinAuth()
        auth.set_pin("OldPin1")
        ok, err = auth.change_pin("OldPin1", "NewPin2")
        self.assertTrue(ok)
        self.assertIsNone(auth.login("OldPin1"))
        self.assertIsNotNone(auth.login("NewPin2"))

    def test_pin_change_wrong_old(self):
        auth = PinAuth()
        auth.set_pin("OldPin1")
        ok, err = auth.change_pin("WrongOld", "NewPin2")
        self.assertFalse(ok)

    def test_force_set_pin(self):
        auth = PinAuth()
        auth.set_pin("OldPin1")
        auth.force_set_pin("Fresh1")
        self.assertIsNone(auth.login("OldPin1"))
        self.assertIsNotNone(auth.login("Fresh1"))

    def test_token_validation(self):
        auth = PinAuth()
        auth.set_pin("Token1")
        token = auth.login("Token1")
        self.assertTrue(auth.validate_token(token))
        self.assertFalse(auth.validate_token("fake-token"))


class PinRateLimitTests(unittest.TestCase):
    def setUp(self):
        self.auth = PinAuth()
        self.auth.max_attempts = 3
        self.auth.lock_seconds = 60

    def test_not_locked_initially(self):
        self.assertIsNone(self.auth.check_locked("10.0.0.1"))

    def test_locked_after_max_attempts(self):
        for _ in range(self.auth.max_attempts):
            self.auth.register_failure("10.0.0.1")
        remaining = self.auth.check_locked("10.0.0.1")
        self.assertIsNotNone(remaining)
        self.assertGreater(remaining, 0)

    def test_lock_expires(self):
        self.auth.lock_seconds = -10
        for _ in range(self.auth.max_attempts):
            self.auth.register_failure("10.0.0.2")
        self.assertIsNone(self.auth.check_locked("10.0.0.2"))

    def test_reset_failures(self):
        for _ in range(self.auth.max_attempts - 1):
            self.auth.register_failure("10.0.0.3")
        self.auth.reset_failures("10.0.0.3")
        for _ in range(self.auth.max_attempts - 1):
            self.auth.register_failure("10.0.0.3")
        self.assertIsNone(self.auth.check_locked("10.0.0.3"))

    def test_ips_are_isolated(self):
        for _ in range(self.auth.max_attempts):
            self.auth.register_failure("10.0.0.4")
        self.assertIsNone(self.auth.check_locked("10.0.0.5"))
        self.assertIsNotNone(self.auth.check_locked("10.0.0.4"))


if __name__ == "__main__":
    unittest.main()
