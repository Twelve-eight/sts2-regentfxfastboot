#!/usr/bin/env python3
"""Isolated control-flow model of ModNoticeWatcher._Process (WS-0916-03).

WHAT THIS IS
------------
A decision-model execution of the watcher's per-frame state machine, transcribed from
mod/RegentFXFastBootCode/ModNoticeWatcher.cs. No Godot node, SceneTree, native callback or
UI is instantiated; nothing here launches the game or Steam. It is a model, not a native
run, and it proves decision-flow properties only - not engine timing, not rendering.

WHY IT EXISTS
-------------
The watcher is attached to the SceneTree ROOT, so leaving the main menu frees nothing. The
defect: after `_menuSeen`, the frame budget is frozen by design and the modal-slot budget is
only reached further down the same function, so a menu that disappears between the two
leaves a root-owned node polling for the rest of the session, with the notice neither shown
nor reported. docs/workspace-state-evidence/2026-09-16-review/notice-lifecycle-model.json
records exactly that state (menuSeen=true, frames=1, attempts=0, freed=false).

This file makes three things checkable instead of asserted:

  1. ANTI-DRIFT. Every limit is parsed out of the C# source, never re-typed here, and the
     shape of `_Process` (the order of its checks) is asserted against the source text. If
     an edit reorders the budgets or renames a constant, the model fails loudly instead of
     quietly modelling a function that no longer exists.
  2. THE DEFECT. The `pre` variant is the pre-fix control flow; it must reproduce the
     recorded evidence state above, including the recorded scenario (menu for one frame,
     then absent for 10000 frames).
  3. THE FIX. The `post` variant must reach a terminal state in every scenario, must keep
     both budgets reachable, must never charge two budgets in one frame, and must never
     record a notice as shown that was dropped.

VARIANTS
--------
  pre                pre-fix control flow: the menu-absent branch returns unconditionally.
  post               the shipped fix: menu departure is terminal after a small tolerance.
  post_no_tolerance  the fix with the departure tolerance forced to 0. Not shipped; it
                     exists to show the tolerance is load-bearing, i.e. that scenario (e)
                     would otherwise regress.

USAGE
-----
  python tools/notice-lifecycle/model.py                      # run scenarios, print table
  python tools/notice-lifecycle/model.py --out results.json   # also write the results file
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable

REPO = Path(__file__).resolve().parents[2]
SOURCE = REPO / "mod" / "RegentFXFastBootCode" / "ModNoticeWatcher.cs"

# The scenario and state recorded by the review that found this defect. The `pre` variant
# must reproduce it exactly, which is what ties this model to that evidence.
RECORDED = {
    "sourceSha256": "136db76663a9a529fc2819bd1929d9454f2e21d58704c810d99d2e547e9be662",
    "scenario": "Menu present for one frame, then absent for 10000 frames; no TryShow call occurred.",
    "modelState": {
        "menuSeen": True,
        "frames": 1,
        "settleFrames": 1,
        "attempts": 0,
        "framesSinceAttempt": 0,
        "freed": False,
    },
}

# Every limit the model needs, read out of the C# so it cannot drift.
CONST_NAMES = (
    "MenuWaitFrameLimit",
    "SettleFrames",
    "ShowAttemptLimit",
    "ShowRetryInterval",
    "MenuDepartureToleranceFrames",
)

HORIZON = 20000  # frames a scenario may run before "no terminal state" is reported


def load_limits(source: str) -> dict[str, int]:
    limits: dict[str, int] = {}
    for name in CONST_NAMES:
        m = re.search(rf"private const int {name} = (\d+);", source)
        if not m:
            raise SystemExit(f"FATAL: {name} not found in {SOURCE}; the model is out of date")
        limits[name] = int(m.group(1))
    return limits


def load_process_body(source: str) -> str:
    """The text of `_Process`, so the shape assertions run against the real function."""
    m = re.search(r"public override void _Process\(double delta\)\s*\{(.*?)\n    \}\n",
                  source, re.DOTALL)
    if not m:
        raise SystemExit(f"FATAL: _Process not found in {SOURCE}; the model is out of date")
    return m.group(1)


def check_source_shape(body: str, limits: dict[str, int]) -> list[str]:
    """Assert the control-flow shape the model transcribes, in the order it must appear.

    A model that silently drifts from its source is worse than no model: it would report
    "bounded" about code that is not bounded. These checks make that failure loud.
    """
    checks: list[str] = []

    def at(needle: str) -> int:
        i = body.find(needle)
        if i < 0:
            raise SystemExit(f"FATAL: _Process no longer contains {needle!r}; the model is out of date")
        return i

    i_frame_budget = at("if (!_menuSeen && ++_frames > MenuWaitFrameLimit)")
    i_menu_null = at("nGame.MainMenu == null")
    i_departure = at("if (++_absentFrames <= MenuDepartureToleranceFrames)")
    i_settle = at("if (++_settleFrames < SettleFrames)")
    i_attempt_interval = at("++_framesSinceAttempt < ShowRetryInterval")
    i_attempt_limit = at("if (_attempts >= ShowAttemptLimit)")
    i_show = at("ModNotice.TryShow(_kind, _warmed)")

    # Budget 1 is checked before the menu is read; budget 2 only after settle. Their relative
    # order is what makes "the first budget is frozen once the menu is seen" true.
    if not i_frame_budget < i_menu_null < i_settle < i_attempt_interval < i_attempt_limit < i_show:
        raise SystemExit("FATAL: _Process check order changed; the model's transcription is stale")
    checks.append("check order: frame budget -> menu read -> departure -> settle -> attempt "
                  "interval -> attempt limit -> TryShow")

    # The departure branch must sit INSIDE the menu-absent branch, must be gated on _menuSeen
    # (so the pre-menu wait still belongs to budget 1), must be tolerant, must log and must free.
    absent_branch = body[i_menu_null:i_settle]
    if not i_menu_null < i_departure < i_settle:
        raise SystemExit("FATAL: the departure branch is not inside the menu-absent branch")
    for needle in ("if (!_menuSeen)", "return;", "QueueFree();"):
        if needle not in absent_branch:
            raise SystemExit(f"FATAL: the menu-absent branch no longer contains {needle!r}")
    if "++_absentFrames" not in absent_branch:
        raise SystemExit("FATAL: the menu-absent branch no longer counts consecutive absences")
    if "went away" not in absent_branch:
        raise SystemExit("FATAL: the menu-absent branch no longer logs a drop reason")
    checks.append("departure branch: inside the menu-absent branch, gated on _menuSeen, "
                  "tolerant, logs a reason and frees")

    # A dropped notice must never be recorded as shown: the only MarkShown call site is inside
    # ModNotice.TryShow, which this watcher reaches only on its success path.
    if "MarkShown" in body:
        raise SystemExit("FATAL: _Process now writes the one-shot state directly; a drop could "
                         "be recorded as shown")
    checks.append("no direct MarkShown in _Process (state is recorded only by TryShow, on the "
                  "success path)")

    if limits["MenuDepartureToleranceFrames"] < 1:
        raise SystemExit("FATAL: the departure tolerance must be at least one frame")
    checks.append(f"departure tolerance is a positive named constant "
                  f"({limits['MenuDepartureToleranceFrames']} frames)")

    return checks


@dataclass
class Env:
    """One frame's outside world.

    menu_present  NGame.Instance is valid AND NGame.MainMenu is not null.
    slot_free     ModNotice.TryShow would return true this frame (container present, slot
                  free, node adopted). A predicate of the frame index, so a scenario can make
                  the slot busy for a while and then free.
    """

    menu_present: Callable[[int], bool]
    slot_free: Callable[[int], bool]


@dataclass
class Watcher:
    kind: str = "LateOrder"
    menu_seen: bool = False
    frames: int = 0
    settle_frames: int = 0
    attempts: int = 0
    frames_since_attempt: int = 0
    absent_frames: int = 0
    freed: bool = False
    shown: bool = False
    state_recorded: bool = False
    drop_reason: str | None = None
    terminal_frame: int | None = None
    # instrumentation, for the invariants
    charges: list[tuple[int, tuple[str, ...]]] = field(default_factory=list)
    violations: list[str] = field(default_factory=list)

    def free(self, frame: int, reason: str) -> None:
        self.freed = True
        self.drop_reason = reason
        self.terminal_frame = frame

    def show(self, frame: int) -> None:
        # Mirrors ModNotice.TryShow: the one-shot state is recorded at DISPLAY time, and only
        # then. A drop never reaches this.
        self.shown = True
        self.state_recorded = True
        self.freed = True
        self.terminal_frame = frame


def step(w: Watcher, frame: int, env: Env, limits: dict[str, int], variant: str) -> None:
    """One frame of _Process. `variant` selects the control flow being modelled."""
    charged: list[str] = []

    # --- budget 1: waiting for the menu. Short-circuited on !_menuSeen, so it is frozen for
    # the rest of the session once the menu has been seen (deliberate; see the class comment:
    # charging it every frame broke the success path).
    if not w.menu_seen:
        w.frames += 1
        charged.append("frames")
        if w.frames > limits["MenuWaitFrameLimit"]:
            w.free(frame, "main menu did not appear within the frame budget")
            w.charges.append((frame, tuple(charged)))
            return

    menu_present = bool(env.menu_present(frame))
    if not menu_present:
        if not w.menu_seen:
            # Not up yet: budget 1 owns this wait.
            w.charges.append((frame, tuple(charged)))
            return
        if variant == "pre":
            # THE DEFECT: once the menu has been seen, every frame without it returns here.
            # Budget 1 is frozen above and budget 2 is below this return, so nothing advances
            # and nothing frees this root-owned node.
            w.charges.append((frame, tuple(charged)))
            return
        tolerance = 0 if variant == "post_no_tolerance" else limits["MenuDepartureToleranceFrames"]
        w.absent_frames += 1
        charged.append("absent")
        if w.absent_frames <= tolerance:
            w.charges.append((frame, tuple(charged)))
            return
        w.free(frame, f"main menu went away (no main menu for {w.absent_frames} consecutive frames)")
        w.charges.append((frame, tuple(charged)))
        return

    # The menu is back (or never left): any absence counted above was transient.
    w.absent_frames = 0

    if not w.menu_seen:
        w.menu_seen = True

    # Let the menu finish its own entry animation and any engine modal it raises.
    w.settle_frames += 1
    charged.append("settle")
    if w.settle_frames < limits["SettleFrames"]:
        w.charges.append((frame, tuple(charged)))
        return

    # One attempt per interval: the modal slot is single and may be transiently held.
    if w.attempts > 0:
        w.frames_since_attempt += 1
        charged.append("framesSinceAttempt")
        if w.frames_since_attempt < limits["ShowRetryInterval"]:
            w.charges.append((frame, tuple(charged)))
            return
    w.frames_since_attempt = 0

    # --- budget 2: the modal slot. This is the bound that actually runs once the menu is up.
    if w.attempts >= limits["ShowAttemptLimit"]:
        w.free(frame, "the engine's modal slot stayed busy for the whole attempt budget")
        w.charges.append((frame, tuple(charged)))
        return
    w.attempts += 1
    charged.append("attempts")

    if bool(env.slot_free(frame)):
        w.show(frame)
    w.charges.append((frame, tuple(charged)))


def run(variant: str, env: Env, limits: dict[str, int], horizon: int = HORIZON) -> Watcher:
    w = Watcher()
    for frame in range(1, horizon + 1):
        step(w, frame, env, limits, variant)
        if w.freed:
            break
    check_invariants(w, limits, variant)
    return w


def check_invariants(w: Watcher, limits: dict[str, int], variant: str) -> None:
    """Properties that must hold in every scenario, on the live counters of the run."""
    # inv1: no frame charges two budgets. Budget 1 only runs while !_menuSeen and budget 2 only
    # runs after the menu is seen, so they can never both advance on one frame.
    for frame, charged in w.charges:
        if "frames" in charged and "attempts" in charged:
            w.violations.append(f"frame {frame} charged both the menu budget and the attempt budget")
        if charged.count("frames") > 1 or charged.count("attempts") > 1:
            w.violations.append(f"frame {frame} charged one budget twice: {charged}")

    # inv2: the menu budget stops the moment the menu is seen, and never runs past its limit.
    if w.menu_seen and w.frames > limits["MenuWaitFrameLimit"]:
        w.violations.append("the menu budget advanced past its limit even though the menu was seen")

    # inv3: one-shot recording follows display, both ways.
    if w.shown and not w.state_recorded:
        w.violations.append("the notice was shown without recording its one-shot state")
    if w.state_recorded and not w.shown:
        w.violations.append("the one-shot state was recorded for a notice that was never shown")
    if w.shown and w.drop_reason is not None:
        w.violations.append("the same run both showed and dropped the notice")

    # inv4: a drop frees the node, never shows, and never records.
    if w.drop_reason is not None and not w.freed:
        w.violations.append("a drop reason was logged without freeing the node")
    if w.drop_reason is not None and w.shown:
        w.violations.append("a dropped notice was also recorded as shown")

    # inv5: the tolerance may only be exceeded on the frame the node frees itself.
    if not w.freed and w.absent_frames > limits["MenuDepartureToleranceFrames"]:
        w.violations.append("menu absence exceeded the tolerance without reaching a terminal state")

    # inv6: the shipped fix is bounded in every scenario.
    if variant != "pre" and not w.freed:
        w.violations.append(f"no terminal state within {HORIZON} frames")


def scenarios(limits: dict[str, int]) -> dict[str, tuple[str, Env, dict[str, dict]]]:
    """(description, environment, expectation-per-variant) for each acceptance scenario.

    Expectations are per variant because the point of the model is the DIFFERENCE between
    them: the `pre` control flow is expected to fail exactly where the defect is (scenario
    b, and nowhere else), and the shipped `post` control flow is expected to pass everywhere.
    A scenario whose `pre` expectation equals its `post` expectation is one the fix must not
    change at all - that is the regression half of the check.
    """
    always: Callable[[int], bool] = lambda _f: True  # noqa: E731
    never: Callable[[int], bool] = lambda _f: False  # noqa: E731

    def menu_from(first: int) -> Callable[[int], bool]:
        return lambda f: f >= first

    def menu_except(absent: set[int], first: int = 1) -> Callable[[int], bool]:
        return lambda f: f >= first and f not in absent

    # What every variant must produce for the paths the fix leaves alone.
    UNCHANGED_MENU_NEVER = {"freed": True, "shown": False, "stateRecorded": False,
                            "terminalFrame": limits["MenuWaitFrameLimit"] + 1}
    UNCHANGED_SLOT_BUSY = {"freed": True, "shown": False, "stateRecorded": False,
                           "attempts": limits["ShowAttemptLimit"]}
    UNCHANGED_SUCCESS = {"freed": True, "shown": True, "stateRecorded": True,
                         "terminalFrame": limits["SettleFrames"]}
    UNCHANGED_TRANSIENT = {"freed": True, "shown": True, "stateRecorded": True}

    return {
        # (a) The pre-menu wait is still owned by budget 1 and still bounded.
        "a_menu_never_appears": (
            "menu never appears -> bounded drop",
            Env(menu_present=never, slot_free=always),
            {"pre": UNCHANGED_MENU_NEVER, "post": UNCHANGED_MENU_NEVER,
             "post_no_tolerance": UNCHANGED_MENU_NEVER},
        ),
        # (b) The recorded defect scenario, extended from 10000 frames to "forever". This is
        # the ONLY scenario where the pre-fix control flow is expected to differ, and the
        # difference is the defect itself: no terminal state, so the node is never freed.
        "b_menu_departs_long": (
            "menu present one frame, then absent for the rest of the session -> bounded drop, freed",
            Env(menu_present=lambda f: f == 1, slot_free=always),
            {
                "pre": {"menuSeen": True, "frames": 1, "settleFrames": 1, "attempts": 0,
                        "framesSinceAttempt": 0, "freed": False, "shown": False,
                        "stateRecorded": False, "terminalFrame": None},
                "post": {"freed": True, "shown": False, "stateRecorded": False,
                         "terminalFrame": 1 + limits["MenuDepartureToleranceFrames"] + 1},
                "post_no_tolerance": {"freed": True, "shown": False, "stateRecorded": False,
                                      "terminalFrame": 2},
            },
        ),
        # (c) Budget 2 is still the post-menu bound and is still reachable.
        "c_slot_always_busy": (
            "modal slot always busy -> bounded drop",
            Env(menu_present=menu_from(1), slot_free=never),
            {"pre": UNCHANGED_SLOT_BUSY, "post": UNCHANGED_SLOT_BUSY,
             "post_no_tolerance": UNCHANGED_SLOT_BUSY},
        ),
        # (d) The normal path is unchanged: settle, then show once.
        "d_normal_success": (
            "normal success -> shown and freed",
            Env(menu_present=menu_from(1), slot_free=always),
            {"pre": UNCHANGED_SUCCESS, "post": UNCHANGED_SUCCESS,
             "post_no_tolerance": UNCHANGED_SUCCESS},
        ),
        # (e) The tolerance: one absent frame during menu construction is not a departure.
        # The pre-fix variant also shows here (an absence that returns costs it nothing), which
        # is why the tolerance is a correctness requirement of the FIX and not a fix for a
        # pre-existing bug: `post_no_tolerance` is what regresses this scenario.
        "e_transient_absence": (
            "transient single-frame menu absence -> notice still shown",
            Env(menu_present=menu_except({2}), slot_free=always),
            {"pre": UNCHANGED_TRANSIENT, "post": UNCHANGED_TRANSIENT,
             "post_no_tolerance": {"freed": True, "shown": False, "stateRecorded": False,
                                   "terminalFrame": 2}},
        ),
        # (e2) The tolerance is a grace, not an amnesty: an absence longer than it still drops,
        # so a real departure can never be absorbed by a too-large tolerance.
        "e2_absence_beyond_tolerance": (
            f"menu absent for {limits['MenuDepartureToleranceFrames'] + 1} frames then back -> "
            "bounded drop (the grace is bounded)",
            Env(menu_present=menu_except(set(range(2, 3 + limits["MenuDepartureToleranceFrames"]))),
                slot_free=always),
            {
                # Pre-fix: the absence is free, so it still shows once the menu returns.
                "pre": {"freed": True, "shown": True, "stateRecorded": True},
                "post": {"freed": True, "shown": False, "stateRecorded": False,
                         "terminalFrame": 1 + limits["MenuDepartureToleranceFrames"] + 1},
                "post_no_tolerance": {"freed": True, "shown": False, "stateRecorded": False,
                                      "terminalFrame": 2},
            },
        ),
        # (f) The retry interval is untouched: the slot frees after a while, so the notice is
        # still shown, but only on the attempt that follows an interval - never on every frame.
        "f_slot_frees_later": (
            "modal slot busy, then free -> shown once on a later attempt (retry interval intact)",
            Env(menu_present=menu_from(1), slot_free=lambda f: f >= 200),
            {
                # Identical in every variant: this path does not involve menu departure at all.
                **{v: {"freed": True, "shown": True, "stateRecorded": True,
                       "attempts": lambda a, _s: a > 1}
                   for v in ("pre", "post", "post_no_tolerance")},
            },
        ),
        # (g) A departure AFTER attempts have begun is still bounded - the case the pre-fix
        # code also gets wrong, because budget 2 lives below the menu check as well.
        "g_departure_after_attempts": (
            "menu seen, slot busy, then the menu departs -> bounded drop, freed",
            Env(menu_present=lambda f: f <= 400, slot_free=never),
            {
                "pre": {"menuSeen": True, "frames": 1, "freed": False, "shown": False,
                        "stateRecorded": False, "terminalFrame": None,
                        "attempts": lambda a, _s: a > 0},
                "post": {"freed": True, "shown": False, "stateRecorded": False,
                         "terminalFrame": 400 + limits["MenuDepartureToleranceFrames"] + 1,
                         "attempts": lambda a, _s: a > 0},
                "post_no_tolerance": {"freed": True, "shown": False, "stateRecorded": False,
                                      "terminalFrame": 401,
                                      "attempts": lambda a, _s: a > 0},
            },
        ),
    }


def state_of(w: Watcher) -> dict:
    return {
        "menuSeen": w.menu_seen,
        "frames": w.frames,
        "settleFrames": w.settle_frames,
        "attempts": w.attempts,
        "framesSinceAttempt": w.frames_since_attempt,
        "absentFrames": w.absent_frames,
        "freed": w.freed,
        "shown": w.shown,
        "stateRecorded": w.state_recorded,
        "terminalFrame": w.terminal_frame,
        "dropReason": w.drop_reason,
    }


def expect_ok(got: dict, want: dict) -> list[str]:
    """Compare a modelled state against an expectation.

    A value may be a predicate over the whole state instead of a literal, for the few
    expectations that are relations rather than numbers (for example "the attempt budget was
    not exhausted" - asserting the exact frame would just re-derive the retry arithmetic the
    model is supposed to be checking).
    """
    bad: list[str] = []
    for k, v in want.items():
        actual = got.get(k)
        if callable(v):
            if not v(actual, got):
                bad.append(f"{k}: predicate not satisfied, got {actual!r}")
        elif actual != v:
            bad.append(f"{k}: expected {v!r}, got {actual!r}")
    return bad


def jsonable(want: dict) -> dict:
    """Render an expectation for the report: predicates are relations, not literals."""
    return {k: ("<predicate>" if callable(v) else v) for k, v in want.items()}


def main() -> int:
    ap = argparse.ArgumentParser(description="Model of ModNoticeWatcher._Process (WS-0916-03)")
    ap.add_argument("--source", default=str(SOURCE), help="path to ModNoticeWatcher.cs")
    ap.add_argument("--out", default=None, help="write the results as JSON to this path")
    args = ap.parse_args()

    source = Path(args.source).read_text(encoding="utf-8")
    limits = load_limits(source)
    shape_checks = check_source_shape(load_process_body(source), limits)

    report: dict = {
        "model": "ModNoticeWatcher._Process decision model (WS-0916-03)",
        "note": "MODEL ONLY. No Godot node, SceneTree, native callback or UI was instantiated; "
                "no game and no Steam was launched. This proves decision flow, not engine timing.",
        "source": str(SOURCE.relative_to(REPO)).replace("\\", "/"),
        "limits": limits,
        "sourceShapeChecks": shape_checks,
        "variants": {},
        "recordedEvidence": {},
        "budgetReachability": {},
        "failures": [],
    }
    print(f"source : {report['source']}")
    print(f"limits : {limits}")
    for c in shape_checks:
        print(f"  [SHAPE] {c}")
    print()

    for variant in ("pre", "post", "post_no_tolerance"):
        print(f"variant: {variant}")
        rows = {}
        for name, (desc, env, want_by_variant) in scenarios(limits).items():
            want = want_by_variant[variant]
            w = run(variant, env, limits)
            st = state_of(w)
            met = not expect_ok(st, want)
            rows[name] = {"scenario": desc, "state": st, "expected": jsonable(want),
                          "expectationMet": met, "invariantViolations": w.violations}
            if not met:
                report["failures"].append(f"{variant}/{name}: {expect_ok(st, want)}")
            # Invariants must hold on EVERY variant: they are properties of the contract, not
            # of a particular control flow.
            report["failures"] += [f"{variant}/{name}: {v}" for v in w.violations]
            flag = "ok" if met else "UNEXPECTED"
            print(f"  {name:28s} menuSeen={str(st['menuSeen']):5s} frames={st['frames']:<5d} "
                  f"attempts={st['attempts']:<4d} freed={str(st['freed']):5s} "
                  f"shown={str(st['shown']):5s} terminalFrame={str(st['terminalFrame']):<6s} ({flag})")
            if st["dropReason"]:
                print(f"      drop: {st['dropReason']}")
            for v in w.violations:
                print(f"      VIOLATION: {v}")
        report["variants"][variant] = rows
        print()

    # --- the recorded evidence, reproduced by the pre-fix variant -------------------------
    print("recorded evidence (notice-lifecycle-model.json)")
    w = run("pre", Env(menu_present=lambda f: f == 1, slot_free=lambda f: False), limits, horizon=10000)
    got = {k: state_of(w)[k] for k in RECORDED["modelState"]}
    match = got == RECORDED["modelState"]
    report["recordedEvidence"] = {
        "scenario": RECORDED["scenario"],
        "recordedState": RECORDED["modelState"],
        "modelState": got,
        "reproduced": match,
        "sourceSha256Recorded": RECORDED["sourceSha256"],
    }
    print(f"  scenario  : {RECORDED['scenario']}")
    print(f"  recorded  : {RECORDED['modelState']}")
    print(f"  modelled  : {got}")
    print(f"  reproduced: {match}")
    if not match:
        report["failures"].append(f"the pre variant does not reproduce the recorded evidence: {got}")
    print()

    # --- budget reachability and disjointness ---------------------------------------------
    a = report["variants"]["post"]["a_menu_never_appears"]["state"]
    c = report["variants"]["post"]["c_slot_always_busy"]["state"]
    d = report["variants"]["post"]["d_normal_success"]["state"]
    reach = {
        "menuWaitBudgetReached (scenario a)": a["frames"] > limits["MenuWaitFrameLimit"],
        "attemptBudgetReached (scenario c)": c["attempts"] == limits["ShowAttemptLimit"],
        "menuWaitBudgetFrozenOnceMenuSeen (c, d)": c["frames"] == 1 and d["frames"] == 1,
    }
    report["budgetReachability"] = reach
    print("budget reachability")
    for k, v in reach.items():
        print(f"  [{'ok' if v else 'FAIL'}] {k}")
        if not v:
            report["failures"].append(f"budget reachability: {k}")
    print()

    if report["failures"]:
        print("FAILURES:")
        for f in report["failures"]:
            print(f"  - {f}")
    else:
        print("PASS: all scenario expectations, invariants, evidence reproduction and "
              "reachability checks hold")

    if args.out:
        Path(args.out).write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        print(f"\nresults written to {args.out}")

    return 1 if report["failures"] else 0


if __name__ == "__main__":
    sys.exit(main())
