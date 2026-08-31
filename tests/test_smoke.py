"""仓库冒烟测试：配置/入口/事件链路。"""

from __future__ import annotations

import pathlib
import tomllib

_ROOT = pathlib.Path(__file__).resolve().parent.parent


def _manifest() -> dict:
    return tomllib.loads((_ROOT / "plugin.toml").read_text(encoding="utf-8"))


def test_plugin_manifest_declares_entry_and_ui():
    m = _manifest()
    assert m["plugin"]["id"] == "neko_fishpower"
    assert m["plugin"]["entry"] == "plugin.plugins.neko_fishpower:NekoFishpowerPlugin"
    assert m["plugin"]["ui"]["enabled"] is True
    for panel in m["plugin"]["ui"]["panel"]:
        entry = _ROOT / panel["entry"]
        assert entry.exists(), f"UI entry missing: {panel['entry']}"


def test_core_modules_compile():
    import ast
    for py in sorted((_ROOT / "core").glob("*.py")) + sorted((_ROOT / "adapters").glob("*.py")):
        ast.parse(py.read_text(encoding="utf-8"), filename=str(py))
    from core.contracts import FishEvent, FishState
    st = FishState()
    assert st.game_running is False
    ev = FishEvent(name="caught")
    assert ev.name == "caught"


def test_arbiter_gates_categories_and_cooldown():
    from core.config_model import FishpowerConfig
    from core.safety_guard import SafetyGuard
    from core.arbiter import Arbiter

    cfg = FishpowerConfig({"dry_run": True})
    safety = SafetyGuard(cfg)
    arb = Arbiter(cfg, safety)
    arb.broadcast_categories = dict(cfg.broadcast_categories)

    # 类别关闭 → 拒绝
    arb.broadcast_categories["chatter"] = False
    ok, _ = arb.decide("chatter")
    assert ok is False, "chatter 类别关闭应拒绝"

    # 上钩（抢占）→ 允许
    ok, reason = arb.decide("caught")
    assert ok is True, reason

    # 同事件冷却 → 拒绝
    ok, reason = arb.decide("caught")
    assert ok is False, "caught 冷却中应拒绝"

    # 玩家说话静默窗 → 拒绝
    arb.on_player_speak(silence_s=60)
    ok, reason = arb.decide("bite")
    assert ok is False, "静默窗内应拒绝"
    assert reason == "player_quiet_window"


def test_runtime_event_renders_caught():
    """caught 事件 → 事实行包含鱼信息。"""
    from core.mood import Persona
    from core.templates import EmotionRenderer

    class _FakeLlm:
        configured = False
        async def call(self, prompt):  # noqa: ARG002
            return None

    persona = Persona(None)
    renderer = EmotionRenderer(persona, llm=_FakeLlm())
    line = renderer.fact_prompt("caught", fish="Cod", weight=3.2, worth=45, extra="")
    assert "Cod" in line and "3.2" in line and "45" in line
