"""Give every git process the tests start a fixed identity, so they pass on a runner without one."""
import os

NAME = "Factory test"
EMAIL = "factory@example.invalid"
VARIABLES = {
    "GIT_AUTHOR_NAME": NAME,
    "GIT_AUTHOR_EMAIL": EMAIL,
    "GIT_COMMITTER_NAME": NAME,
    "GIT_COMMITTER_EMAIL": EMAIL,
}

os.environ.update(VARIABLES)

# A develop worktree set for this machine must never reach a test: without the variable, each test's
# temporary factory root gets its own, next to it (tests that need another folder set it themselves).
os.environ.pop("FACTORY_DEVELOP_DIR", None)
