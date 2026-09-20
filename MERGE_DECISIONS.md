# Origins, Merge Decisions, and Compatibility Boundaries

## Production baseline

`MiniMetro_ExtremeAutoPlanner` is the only production runtime. It reads Mini Metro's native `Game`, `City`, `Station`, `Line`, and asset objects, then submits route operations through the native `LineBuilder`. It does not depend on screen resolution, language, window position, screenshot templates, or an external message queue.

The locally tested game version was:

- Steam App ID: `287980`
- Steam build ID: `25333770`
- Unity: `2022.3.62f2 (7670c08855a9)`
- Original local `Assembly-CSharp.dll` SHA-256: `1A2571454467EE6B5F0353C5513199321FDE089055023758A1464EFC6FD79872`

These values document one verified environment; they are not distributed artifacts or a compatibility guarantee.

## Input projects and traceability

| Input | SHA-256 | Decision |
| --- | --- | --- |
| `mini-metro-master.zip` | `44CA2B677BFAE613F2EA2BFC00D9C0C09EE4700AC0D35782D92A18A9BC49A154` | Read legacy labels through an isolated research layer; do not import its obsolete runtime control stack. |
| `SerpentAI-dev.zip` | `203D225EC2EDF8DA521548B93C3B7554738C254EA4CC89ED3934C28AFF54712E` | Reimplement the useful frame, bounded-buffer, and transform-pipeline boundaries without the old service dependencies. |
| `metro-master.zip` | `E7E548A605B5A8827CA75BE94844FCE1BFB8AEBCAB3B8594469A18AC2E31DA73` | Reimplement octilinear distance, route sequences, neighbourhood search, and transfer reachability as deterministic online planning. |
| `metrosolver-master.zip` | `04AEBD6F182C0BFBCA42262F3164AFA558E570BDBA6AC4AC1E29B71311CDC6AB` | The archive contains a design note but no solver source. Its minimum-cost-network direction became an auditable passenger-flow relaxation. |
| Existing planner | This repository | Preserve as the sole native action and legality baseline. |

The original archives were not modified or copied into the game directory. They are not part of this public source repository.

## Ideas retained

- Native game-object access, route construction, resource allocation, and in-game enable/disable state.
- A narrow patcher boundary: one hook in `Game.Update(float)`, with planner logic in a separate assembly.
- Backups, structural verification, build hashes, session telemetry, and post-session analysis.
- Historical task separation between observation, state, decision, and action.
- A small Serpent-inspired `GameFrame`, bounded frame buffer, and transformation pipeline with no network service.
- Mini Metro's octilinear geometry, route-sequence neighbourhoods, transfer reachability, and candidate-improvement scoring.
- The proposal to model station demand and routes as a cost network, using native station/queue/vehicle state instead of OCR coordinates.

## Ideas rejected from production runtime

- 2018-era FastAI pickles, TensorFlow 1 wrappers, fixed screen coordinates, screenshot-driven input, and simulated mouse control.
- Empty detector factories, calls through `None`, incomplete policy paths, and obsolete framework APIs.
- Redis, Crossbar, Luminoth, deprecated CLI commands, binary version locks, and network listeners with unsafe defaults.
- Merging two competing state sources or input controllers into one process.
- Random initialization and simulated annealing inside the live decision loop. The offline notebook remains useful research context, but runtime actions must be reproducible and bounded.
- Claiming an out-of-kilter implementation from `metrosolver-master`; the archive only proposed it. This project explicitly implements a shortest-path demand-allocation relaxation instead.

## Route and passenger-flow integration

- `RouteFlowOptimizer` models stations as nodes and adjacent route segments as weighted edges.
- Edge cost incorporates octilinear track length, locomotives, carriages, route size, and line queue pressure.
- Queued demand is routed to the cheapest reachable destination shape after deterministic all-pairs shortest-path relaxation.
- The analysis reports average cost, served demand, unreachable demand, and congestion penalty.
- New lines, extensions, transfers, and station rehoming compare baseline and hypothetical objectives to produce `flowGain`.
- New-line search enumerates bounded two- and three-station neighbourhoods. After a network exists, candidates must contain both an unconnected station and a connected transfer station.
- Upgrade scoring uses the same model: unreachability raises line priority, high reachable cost raises line/interchange priority, and congestion raises train/carriage priority.
- Logs expose `flow=[cost,unreach,cong]`, `flowGain`, and `octileLength`.

## Runtime fixes retained in the public source

- Route operations are classified so only a crossing failure that truly blocks an unserved station becomes a global resource need.
- Crossing necessity is recorded before editing state can alter line counts.
- Nonessential `NoCrossing` failures back off the exact edge instead of permanently occupying reward logic.
- Transfer and interchange actions use global cooldowns, evaluation windows, and reverse-move protection.
- Builds use the matching original local assembly rather than patching an already injected assembly again.
- Build/install manifests record base, plugin, and patched hashes; installation rejects a changed game build.
- The standard-library log analyzer detects transfer churn, interchange churn, `NoCrossing` storms, duplicate route labels, and abnormal recovery.

## Vision research boundary

- The sidecar provides NumPy prototype classification, OpenCV station detection, client-area capture, JSONL observations, and atomic control snapshots.
- The C# plugin consumes a fresh advisory snapshot before each decision. A confident scene/count contradiction pauses topology edits while retaining native emergency capacity relief.
- A missing, disabled, invalid, or older-than-six-seconds snapshot falls back to the native policy.
- Dataset tooling converts legacy labels into directory classification and COCO detection layouts while keeping the historical test split isolated.
- Three self-contained Colab notebooks cover audit, TensorFlow/KerasCV, and PyTorch/torchvision experiments with ImageNet/COCO initialization.
- Model bundles include configuration, history, metrics, native weights, and SHA-256 manifests. Integrity does not imply promotion eligibility.
- During trained-model initialization, a `model_loading` heartbeat is published every 0.75 seconds so the plugin stays in `HoldContext` instead of acting through a snapshot-expiry gap.

## Recorded verification

The classical historical held-out evaluation produced:

- context classification: 24/24 correct;
- station detection: TP=278, FP=0, FN=8;
- precision=1.0, recall=0.9720, F1=0.9858;
- matched-box shape accuracy=0.8993.

The strengthened game smoke test deliberately published a visual count of 99 against a native count of 3, observed `HoldStationMismatch` with no action, recorded five `HoldContext(model_loading)` heartbeats, and then required the first `Aligned` event to precede the first planner action. The first selected route logged `flowGain=719.9`, `octileLength=671`, and reduced modeled unreachable demand from 1.00 to 0.00. Five planner actions completed before the test restored the production assembly and verified hashes.

## Compatibility boundary

The scripts validate source compilation, the injected entry point, manifests, model bundles, and regression tools. They cannot guarantee compatibility with future closed-source game updates. Rebuild and repeat structural/runtime verification after every Steam update; never force-install an assembly that fails validation.

