# ArteryCore

An SPT mod that makes shot placement matter by adding **arterial bleeding** to the
major superficial arteries and **deterministic fractures** to the bones, while
leaving every other hit exactly as vanilla resolved it.

ArteryCore never rescales the damage of a bullet. A hit that does not sever an
artery behaves identically to the base game.

## What it does

| System | Behaviour |
| --- | --- |
| **Artery hitboxes** | A penetrating hit on a neck, forearm or thigh collider rolls a per-zone chance to sever the artery. Severed arteries bleed a large amount of HP on top of the normal bullet damage. |
| **Bone hitboxes** | Capsules are laid along the real humerus, radius, femur, tibia and spine transforms. Striking one applies a fracture deterministically, and suppresses the random vanilla fracture roll for that shot. |
| **Bruising** | When armor fully stops a round, the wearer takes a bruise: a pain effect, a stamina-regeneration penalty and a movement-speed penalty that decay over 15 seconds. |
| **Direct damage / total wound damage** | The bullet keeps its full vanilla post-armor damage (the *direct* damage). The arterial blood-loss *budget* is sized as a multiple of that same post-armor figure, so bigger rounds open worse arteries. |
| **Impact shock** | Every hit the local player takes stacks a screen-edge vignette and a brief pain effect, whether or not the round got through armor. |
| **Blood, audio and hit presentation** | Arterial spray is simulated as world-space particles that splat into real blood decals; the impact point is stained on the body mesh; repeated bleed-tick grunts and screen blood are throttled; bleeding out favours the agony death line. |

## What it deliberately does not do

Cut from the TraumaCore feature set on purpose:

- No corpse wound inspection
- No corpse dragging
- No death-screen wound report (the vanilla screen is untouched)
- No treatment changes (vanilla bandages, tourniquets and stims work exactly as they do in the base game)
- No heart or brain organ hitboxes, no instant-kill head volumes
- No custom bullet fragmentation

## Artery zones

Detection is **collider-gated**: any penetrating hit on one of these
`EBodyPartColliderType` colliders can sever the artery, subject to a minimum
wound-depth gate and a per-bullet chance roll.

| Zone | Colliders | Drains | Default chance | Enabled |
| --- | --- | --- | --- | --- |
| Carotid / jugular | `NeckFront`, `NeckBack` | **Chest** | 80% | yes |
| Femoral | `LeftThigh`, `RightThigh` | Leg | 60% | yes |
| Radial / ulnar | `LeftForearm`, `RightForearm` | Arm | 40% | yes |
| Brachial | `LeftUpperArm`, `RightUpperArm` | Arm | 35% | no |
| Femoral at groin | `Pelvis`, `PelvisBack` | Stomach | 50% | no |

The **Drains** column is what EFT itself reports for that collider, confirmed
from live logs — the collider-to-pool binding is serialised in the player
prefab, not in managed code, so it is never hard-coded here. The neck resolving
to Chest rather than Head is the surprising one, and it makes the carotid the
only artery wired straight into a pool that can kill.

Each zone has its own **Enabled**, **Chance** and **Severity** setting. The last
two zones ship disabled so the live set is exactly the three requested ones.

The roll is seeded from the shot identity plus the impact point, so it is stable
if a shot is evaluated twice but varies freely between shots — and every shotgun
pellet rolls independently.

Note that EFT has no foot or hand collider: the calf collider is the entire lower
leg and the forearm collider runs to the fingertips.

## Bleed model

```
budget   = postArmorDamage x BleedBudget x ZoneSeverity x depthScale x targetMultiplier
           ------------------------------------------------------------------------
                              deliveryMultiplier (routing)

severity = budget / area(BleedDuration, FrontLoad)          HP/s at t=0
rate     = severity x strength(t) x escalation(activeWounds)
```

`depthScale` falls off for shallow wounds and rises for a through-and-through, so
a graze bleeds far less than a clean pass-through.

`FrontLoad` shapes the curve: `0` is a flat rate for the whole duration, `1` puts
most of the blood loss in the first few seconds. The total is the same either way.

### Escalation

Every additional severed artery on a target speeds up **the whole stack**, not
just the newest wound — a third severed artery accelerates the first two as well.
At the AI default of `0.25`:

| Active wounds | 1 | 2 | 3 | 4 | 5 | 6 |
| --- | --- | --- | --- | --- | --- | --- |
| Rate multiplier | 1.00x | 1.25x | 1.50x | 1.75x | 2.00x | 2.25x |

Capped by `Escalation Ceiling` (default 3.00x).

### Two vanilla rules that shape all of this

`ActiveHealthController.TryToKillAfterDestroyPart` is four instructions:

```
if (IsBleeding(damageType)) return;
if (bodyPart == Head || bodyPart == Chest) Kill(damageType);
```

1. **Only a destroyed head or chest kills.** Blacking a stomach or a limb never
   kills, at any amount. Blood loss confined to a leg is absorbed by the leg and
   then by the 70 HP stomach and dead-ends without threatening anything lethal.
2. **Bleeding damage is explicitly excluded from that kill.** Vanilla will let a
   bleed drive the chest to zero and leave the victim standing there with a
   destroyed chest, indefinitely. In base Tarkov that is fine — bleeds are a tax
   you bandage and bullets do the killing. For this mod it is fatal, so
   `ArterialBleedOutDeathPatch` restores the obvious outcome, but only for a
   target carrying an open arterial wound.

### Systemic blood loss

Rule 1 is why systemic exists.

`Systemic Blood Loss` diverts a share of every arterial tick straight to the
chest, bypassing the limb and stomach entirely. This is the physiologically
honest model — you bleed out from lost blood volume, not from your leg running
out of hit points — and it is the only route by which a limb artery can kill.

Whatever the routing, the budget is divided by the delivery multiplier first, so
the configured **Total Blood Loss** is what actually lands.

## Player and AI are tuned separately

Numbers that make a bot bleed out in five seconds are unplayable pointed back at
the player, so arterial bleeding has two independent profiles. **PMC bots and
scavs both inherit the AI profile**, differing only by a blood-loss multiplier.

| | Players | AI |
| --- | --- | --- |
| Total Blood Loss | 1.15 | 1.15 |
| Bleed Duration | 25s | **12s** |
| Front Load | 0.25 | 0.20 |
| Systemic Blood Loss | **0** | **0.50** |
| Escalation Per Wound | **0** | **0.25** |
| Stack Cap | 4 | **8** |
| Artery Chance Multiplier | 1.0 | **1.4** (60% femoral becomes 84%) |

`Stack Cap` is per body part, and opening more evicts the oldest wound *along
with its unspent blood loss* — a low cap silently punishes concentrating fire on
one limb. Escalation has its own ceiling, so the cap can sit high safely.

The player defaults are deliberately the shipped 1.0.0 values — human TTK is
unchanged. Systemic and escalation exist on the player side too but default to
`0`; raise them if you want the mechanics pointed at yourself.

### Measured time to death, thigh hits

Time from the last round landing, all shots severing, no self-treatment:

| Thigh severs | 1 | 2 | 3 | 4 | 5 | 6 |
| --- | --- | --- | --- | --- | --- | --- |
| Scav | alive | alive | **6.1s** | 3.2s | 1.5s | 0.4s |
| PMC bot | alive | alive | **6.1s** | 3.2s | 1.5s | 0.4s |
| Raider / Gluhar follower | alive | alive | alive | 7.6s | 4.6s | 2.6s |
| Killa | alive | alive | alive | 9.7s | 5.9s | 3.6s |
| **Player** | alive | alive | alive | 24.1s | 19.1s | 15.7s |

At the AI chance multiplier, 4 rounds into the thighs gives an 88% chance of at
least 3 severs, and 5 rounds gives 97%.

These are simulator figures calibrated against live `[BleedTick]` logs. The
simulator ran roughly 10-15% optimistic against the real game during tuning, so
the shipped defaults carry deliberate margin rather than sitting exactly on a
5-second target.

Every arterial wound is surfaced as a **real vanilla Heavy Bleeding effect**, so
the health panel, the bleed icon and all vanilla treatment items behave normally.
The native effect's own damage is zeroed — ArteryCore owns the damage curve — and
its tooltip is relabelled to name the artery and show the live HP/s.

### Linkage

Vanilla stops applying damage to a blacked limb, so a femoral bleed on its own
would black a leg and then stop, killing nobody. Linkage bleeds a share of a
limb's blood loss into the torso, and redirects it entirely once the limb is
destroyed. **This is what makes limb arteries lethal at all.** It only ever
applies to ArteryCore's own arterial damage, never to bullet damage. Turn it off
under `07 - Linkage` if you want arteries to be non-fatal on limbs.

## Configuration

All settings live in the in-game Configuration Manager under
`com.arterycore`, grouped as:

| Section | Contents |
| --- | --- |
| `00 - Default Preset` | One button to restore every curated default |
| `01 - Feature Toggles` | Master switch per subsystem |
| `02 - Artery Zones` | Per-zone enable, base chance, severity, plus the depth gate |
| `03 - Bleeding (Players)` | Budget, duration, front load, systemic, escalation, stack cap, treatment, chance and damage multipliers |
| `04 - Bleeding (AI)` | The same set for AI, plus per-class scav and PMC-bot blood-loss multipliers |
| `05 - Bone Fractures` | Limb and spinal fractures, fracture pain, capsule radii |
| `06 - Bruising` | Duration, stamina penalty, speed penalty |
| `07 - Impact Shock` | Vignette intensity, ceiling, fade, pain duration |
| `08 - Linkage` | Per-limb linkage strength and blacked-part retention |
| `09 - Target Balance` | Whether players and AI are affected at all |
| `10 - Debugging` | Logging, per-shot classification, bleed diagnostics, force-artery-hits |

**Config files are never overwritten by new defaults.** If a release retunes
something, click **Restore Recommended Defaults** in `00` or your existing values
stay in force.

Turn on **Logging** plus **Log Every Shot** to see the collider, wound depth,
penetration, roll value and outcome for every bullet — that is the tool for
tuning the chances and the depth gate.

## Balance notes

Three places worth watching when you tune:

- **Shotguns.** Every pellet rolls independently, so a close-range buckshot hit
  to a thigh will sever the femoral several times over — and escalation compounds
  it. `Stack Cap` (default 4) and the femoral `Severity` are the levers.
- **Neck.** The carotid drains the *chest*, not the head — so unlike every other
  zone it feeds a lethal pool directly, with no stomach in the way. At 80% chance
  and 1.35 severity it is by far the strongest artery in the mod. Lower its
  chance or severity first if neck hits feel too decisive.
- **Bots bandage.** `Treatment Effectiveness` is 1.0 by default (vanilla
  semantics), so any bandage clears the arterial wound outright. A bot that heals
  mid-bleed zeroes everything. Lower the AI value if bots undo arterial hits too
  easily, and watch the log for the treatment line when testing.

### Version notes

- **1.1.0** — added the systemic route, escalation, and separate player/AI
  profiles. Limb arteries previously dead-ended in the stomach and could not
  kill at all.
- **1.2.0** — Fika compatibility. `Player.ApplyShot`, `ApplyDamageInfo` and
  `OnDead` are all `virtual`, and Fika replaces every player and bot with
  subclasses that override them without calling base. Patching only `EFT.Player`
  silently did nothing, so the entire shot pipeline had never run under Fika.
  `VirtualPlayerPatches` now discovers and patches every declared override.
- **1.2.2** — `Bleed Diagnostics`, which is what made the remaining problems
  measurable instead of guesswork.
- **1.3.0** — retuned AI from live logs: systemic 0.25 to 0.50 (the stomach was
  eating most of the budget), duration 8s to 12s, stack cap 4 to 8.
- **1.3.1** — the one that actually mattered. Vanilla refuses to kill on a body
  part destroyed by *bleeding* damage, so bots were draining to a 0 HP chest and
  standing there indefinitely. `ArterialBleedOutDeathPatch` restores the kill.

Human TTK has been unchanged throughout.

## Building

Requires .NET SDK and an SPT install. `SptRoot` defaults to
`E:\DEV_ENV\SPT_DEV\4.1.X`; override it if yours differs.

```bash
dotnet build Client/ArteryCore.Client.csproj -p:SptRoot=E:\DEV_ENV\SPT_DEV\4.1.X
```

A Debug build deploys the DLL straight into `BepInEx\plugins\ArteryCore`. A
Release build additionally writes a distributable zip to `dist/`.

## Scope and co-op

ArteryCore hooks `Player.ApplyShot` — but that method is `virtual`, and co-op
frameworks such as Fika replace every player and bot with their own `Player`
subclasses whose overrides never call base. Patching only `EFT.Player` therefore
does nothing at all in those setups.

`VirtualPlayerPatches` walks every loaded assembly, finds every subclass of
`Player` that declares its own `ApplyShot` / `ApplyDamageInfo` / `OnDead`, and
patches each one. A `SoftDependency` on `com.fika.core` forces Fika to load first
so its types exist at scan time. At startup you should see:

```
Patched ApplyShot on 5/5 implementation(s): Player, ClientPlayer, FikaBot, FikaPlayer, ObservedPlayer
```

If `ApplyShot` reports only `Player`, the scan ran before the other assembly
loaded and nothing will fire.

Remote humans in a co-op session are skipped deliberately: a player that is
neither yours nor AI is authoritative on their own client, which runs its own
copy of ArteryCore, so simulating them here would double every arterial wound.

## Credit

Derived from [TraumaCore](https://github.com/Hysocs) by Hysocs, Apache License
2.0. See `NOTICE` for the parts this project builds on.
