# Adherence rubric — v1

The adherence engine grades **process, not outcome**. Every closed trade is
scored against the plan that existed *before* entry. The rubric is versioned:
scores store the rubric version they were computed with, so historical grades
never change when the rubric evolves.

## Scoring

Start at **100** and subtract deductions. Score floors at **0**.

| # | Violation | Deduction | Notes |
| --- | --- | --- | --- |
| 1 | **No plan before entry** | −40 | No trade plan existed at entry time |
| 2 | **Size exceeded** | −20 | Position size above the plan's max size |
| 3 | **Stop widened** | −25 | Stop moved further from entry after placement |
| 4 | **Entry without trigger** | −15 | Self-reported: entered before the planned trigger condition |
| 5 | **Early exit** | −10 | Exited before target/stop without a planned exit condition |
| 6 | **Revenge window** | −20 | Entry within 30 minutes of closing a losing trade |
| 7 | **Traded through red calendar event** | −15 | Held/opened through a high-impact economic event |
| 8 | **Circuit-breaker override** | −25 | Traded while a personal circuit breaker was tripped |
| 9 | **Heat cap exceeded** | −15 | Portfolio heat above the configured cap at entry |

Additional rules:

- **Floor**: the final score is never below 0.
- **Unplanned cap**: a trade with no pre-entry plan (violation 1) is capped at
  **60** regardless of other behavior — an unplanned trade can never grade A/B.
- Deductions stack (multiple violations all apply, subject to floor/cap).

## Grades

| Grade | Score |
| --- | --- |
| **A** | ≥ 90 |
| **B** | 75 – 89 |
| **C** | 60 – 74 |
| **D** | 40 – 59 |
| **F** | < 40 |

## Versioning

- This document defines **v1**. Any change to deductions, thresholds, caps, or
  grade bands requires a new version (v2, …) and a new document.
- The engine implementation lives in `Edgewise.Domain` (pure, fully unit and
  property tested); stored scores carry `RubricVersion` so re-grading is an
  explicit, opt-in operation.
