# alizea1337's Dalamud Plugins

A custom Dalamud repository for my plugins. Each plugin lives under `plugins/<Name>/` and is
built and published separately by GitHub Actions.

## Install

Dalamud Settings → Experimental → Custom Plugin Repositories → add:

```
https://raw.githubusercontent.com/alizea1337/DalamudPlugins/main/repo.json
```

## Plugins

| Plugin | Description | Status |
|---|---|---|
| [Keystroke](plugins/Keystroke) | Automates Keybound Brawler typing with humanized input, auto-queue and reward tracking. | testing |

## Releasing

Push a tag named `<Plugin>-v<version>` (for example `Keystroke-v0.1.0`). The workflow builds the
plugin with the Dalamud SDK, attaches `latest.zip` to a GitHub release, and rewrites `repo.json`
on `main`. Tags containing `-testing` (e.g. `Keystroke-v0.1.0-testing`) publish to the testing
track only.

## License

MIT unless a plugin folder says otherwise.
