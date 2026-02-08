## README for Plugin Info


**TowCars plugin**

I wanted to give some more utility to cars in rust. Sometimes I want to pull other cars around and this plugin will let you do that. 
First take out your hammer 
Next middle click on the car you are pulling with
Then middle click on the car you want to tow
To remove the hook, just middle click on one of the two cars.

---

## What’s new in 2.0.0 (since 1.1.x)

### For players/admins
- **More predictable rope behavior:** rope length is computed at link time (based on current distance) and clamped between `RopeLengthMin` and `RopeLengthMax`.
- **Improved towing stability:** optional yaw alignment helps the towed car point in the direction of the tow line.
- **Stronger “unstick” behavior:** optional winch force helps overcome static friction when the rope is stretched and the towing car is moving.
- **Better unmanned towing:** optional “free roll” wheel handling (brake release + reduced friction stiffness) makes towed cars roll easier.
- **Simplified rope visuals:** rope is drawn as a straight line with endpoints for clarity, and only renders for nearby players within `RopeVisibleDistance`.

### For developers/maintainers
- **Simplified config surface:** grouped settings for tool, rope physics, wheel behavior, alignment, winch, and visuals.
- **Safer cleanup:** active links are released on unload and on break/separation checks, restoring wheel/drag settings.
- **Cleaner link state tracking:** a single link record tracks towing + towed IDs, joint, rope length, and original wheel/rigidbody values.

### Migration notes (1.1.x → 2.0.0)
- Rope visuals config has changed: older “audience/max viewers” and sag/segments options are not used; use `RopeVisibleDistance`, `RopeColor`, and `RopeDrawInterval`.
- Auto-extend rope behavior and the old “assist when taut” loop are not part of 2.0.0; the new assist model is yaw alignment + optional winch force.
- Brake easing/persist options were replaced by the “free roll” wheel settings (`FreeRollTowedWheels` + stiffness/brake torque controls).



### About TowCars
Usage

Equip a Hammer (or configured Tow Hook tool).
Middle-click (Mouse3) on the first car to set the first hook.
Middle-click on a second car to complete the link.
A tow rope will appear, and the cars are now connected.

Releasing

Middle-click on either of the linked cars to release the tow strap.
If cars drift too far apart, the strap will automatically break.

Canceling Selection

If you’ve set the first hook but change your mind, press Reload (R) to cancel selection.

To grant permission to a specific player:
```
oxide.grant user <player_name> towcars.use
```
To grant permission to a group:
```
oxide.grant group <group_name> towcars.use
```
To remove permission from a player:
```
oxide.revoke user <player_name> towcars.use
```
To remove permission from a group:
```
oxide.revoke group <group_name> towcars.use
```
The plugin will automatically check for permissions when the command is used and will enforce any configuration restrictions that are set.
