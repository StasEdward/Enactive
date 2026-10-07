# Enactive

A person states a task; a strong model plans it, a weaker one carries the steps out, a strong one reviews
each step, and the engine decides what the run came to.

## Starting a run

**Host**:
A place a run is started from and watched through: the desktop window, a background run, a phone, the
console. Hosts differ only in who answers a run's questions, where its events go, and how it is stopped.
_Avoid_: client, front end, launcher

**Run request**:
What a host asks to be run: the workspace and its workspace defaults, the request text, where it came from, and optionally the task
it continues, the saved task it is, or the interrupted run it resumes.
_Avoid_: job, command

**Workspace defaults**:
The autonomy level and the worker chosen for a folder. They govern a run in that folder unless the run
names a worker of its own or its saved task does; a task from a phone is governed by the defaults of the
folder it names, not of the one open on the desktop.
_Avoid_: workspace settings, run settings

**Attended run**:
A run with someone at the screen who can see and apply its changes as it goes; only an attended run may
stage its changes.
_Avoid_: interactive run, foreground run
