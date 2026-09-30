# Enactive — notes for coding agents

- **What the engine does.** A person states a task. A strong model plans it and splits it into small steps; a weaker,
  often local, model carries the steps out; a strong model reviews each step and gives a verdict - done or not, on what
  the recorded tool calls and the files show. The engine decides the run's outcome from those verdicts and its own
  checks.
- **It is a general engine.** Tasks can be anything: code, documentation, a report about the machine, mail. Fix the
  engine for the kind of problem, never for the one task that showed it - no rule that only one scenario needs, and
  nothing tied to one language, ecosystem or project layout.
- **A change comes with a test** that fails without it. Behaviour that only a live model shows is measured with the
  benchmark in `bench/` (see `bench/README.md`).
- **Say why in the code.** A limit, a rule or a workaround carries a comment on what went wrong without it.
