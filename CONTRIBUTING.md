# Contributing

Thank you for helping improve Mini Metro AI Planner.

## Ground rules

- Do not commit Mini Metro binaries, decompiled game source, saves, profile data, screenshots, proprietary assets, or Steam files.
- Do not commit training datasets or model weights unless their redistribution rights have been documented and reviewed.
- Keep the C# plugin as the only component that performs game actions. The vision sidecar must remain read-only and may publish only short-lived advisory state.
- Preserve Classic/Extreme legality checks and the fail-safe native fallback.
- Explain changes to planner scoring with a before/after log excerpt or a deterministic test.

## Development workflow

1. Fork the repository and create a focused branch.
2. Run the Python tests:

   ```powershell
   python -m unittest -v .\tools\test_analyze_player_log.py
   cd .\research\vision
   python -m unittest discover -s .\tests -v
   python .\colab\validate_notebooks.py
   ```

3. If you own Mini Metro on Windows, build against your local installation and run `verify.ps1`.
4. Never attach the generated `Assembly-CSharp.dll` to a pull request.
5. Describe the game build, mode, city, and relevant planner log events when reporting runtime behavior.

## Pull requests

Keep changes small enough to review. Include the motivation, affected decision path, verification performed, and any safety/fallback implications. Model changes must report held-out metrics and must not lower promotion gates merely to make a run pass.

