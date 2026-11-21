# Amethyst: Combat, entities, tools and flight — manual test plan

Target client: **Minecraft Java Edition 26.3 (protocol 777)**. These six feature
branches intentionally layer on the durability work; do not test them against an
older client/protocol or a branch missing their parent features.

## Branch/PR sequence

1. `feature/entities-combat` — PR #11, based on `feature/comprehensive-durability` (PR #10)
2. `feature/shield-defence` — PR #12
3. `feature/projectile-physics` — PR #13
4. `feature/living-entity-interactions` — PR #14
5. `feature/fishing-system` — PR #15
6. `feature/elytra-flight` — PR #16

The last branch contains all earlier changes. To test *everything* together,
check out `feature/elytra-flight` with `git fetch --all`, then build/run with
the repository's existing `./run.sh`. **Back up world and Shard data first**
if moving from an older save format. Features are deliberately not merged into
`main` until review.

### Recommended test configuration

Use `defaultGameMode: survival` in `server.yaml` for two-player survival
testing. The trusted server console accepts `summon sheep [online player]`
and `enchant <player> unbreaking [1-3]` or `enchant <player> mending`.
In-game `/summon sheep` and `/enchant` require operator level 2 in online
mode. You can use Creative inventory to obtain items, then return to Survival
for wear tests. Watch for actual slot updates and preserved durability bars.

### 1. Entity and combat

- Connect two players. Verify each sees the other.
- In Survival, hit the other player with a sword. Health should fall;
  attack spam should not cause arbitrary extra damage, and the sword
  should lose durability on confirmed hits.
- Equip armor and repeat: health damage should be lower, and armor should wear.
- Attack from beyond three blocks, or target a Creative player: no valid hit.
- Spawn sheep, hit it and verify health and mutton/wool drops when killed.
- Verify health persists on reconnect, and death/respawn restores health.

### 2. Shields

- Equip a shield in the offhand (or mainhand), hold use and have another
  player attack **from the front**. Health should stay unchanged, shield
  durability should decrease.
- Attack from behind: shield should not block.
- Hit a raised shield with an axe: it should disable for five seconds.
- Release use and attack again: no block.
- Check the shield can break after enough wear.

### 3. Projectiles

- Equip a bow and arrows. Hold, release and check an arrow flies and is
  deducted from inventory. The bow wears.
- Fire at the other player and at a sheep; observe damage. Try blocking
  the arrow with an active shield.
- Throw a trident and recover it. The recovered stack should retain damage.
- Throw snowballs, eggs and ender pearls; watch the projectiles and
  consumption, including server-authoritative pearl teleport.
- Test a crossbow charge/release and the server's ammo checks.

### 4. Shearing

- Spawn a sheep near you with `summon sheep`. In Survival, right-click it
  with shears: wool drops physically, shears lose one durability.
- Try shearing it again immediately: no second wool and no additional wear.
- Wait for timed regrowth, shear again, and verify the sheep's sheared state
  survives a server restart.
- Repeat in Creative: no shears durability loss.

### 5. Fishing

- With a fishing rod, right-click water. A bobber should appear; wait for a
  bite, then reel to generate fish/junk/treasure and an XP orb.
- Reel before a bite: nothing caught; rod still wears once when reeling.
- Inspect physical item pickup and Mending from the resulting XP.
- Try fishing in Creative: durability stays unchanged.
- Disconnect with an active bobber: it should disappear.

### 6. Elytra

- Equip an Elytra in the chest armor slot. Jump off a suitable height
  and start gliding. The wings should wear once per second in Survival,
  potentially prevented by Unbreaking.
- Use a firework rocket while gliding. One rocket should be consumed and
  the player should receive a velocity boost.
- Land: gliding should stop, and the client should leave the glide animation.
- Reduce Elytra to one usable durability point; it should become unusable
  **without disappearing** from the inventory.
- Try Creative flight with Elytra: no durability consumption.
- Teleport-size movement packets while gliding should be rejected and
  corrected rather than accepted unchecked.

## Automated verification

The solution's BlockTests now includes `CombatFeatureTests`,
`ShieldFeatureTests`, `ProjectileFeatureTests`,
`EntityInteractionFeatureTests`, `FishingFeatureTests`,
and `ElytraFeatureTests`. GitHub Actions runs the .NET 10 build,
ShardTests, and BlockTests on every branch commit.

## Known functional limits / follow-ups

These milestones provide playable initial implementations, **not full
vanilla parity**. In particular:

- Only sheep have a persistent, interactable livestock model so far;
  no complete passive mob spawning/AI, breeding, hostility, or pathfinding.
- PvP uses simplified melee cooldown/damage and armor rules;
  food/hunger, complex potion effects, mob combat AI, and a full damage
  type/attribute system are not complete.
- Projectiles cover common arrows/tridents/throwables but not every
  enchantment (Multishot, Piercing, Loyalty), tipped arrow effect,
  splash potion effects or full vanilla crossbow load state.
- Fishing has physical bobbers and weighted basic loot. Lure/Luck of the
  Sea enchantments, complete data-driven fishing loot tables, hooked-entity
  towing and weather-specific bite timers are not complete.
- Elytra uses the vanilla client's glide movement with server-side
  plausibility checks, one-second wear and rocket impulse, rather than
  a full authoritative collision/anti-cheat flight simulator.
- Full enchanting-table mechanics and the complete equipment/enchantment
  catalog are independent work from the durability PRs.

Please test with an isolated world and record the client version, game mode,
reproduction steps, and server log when filing issues.
