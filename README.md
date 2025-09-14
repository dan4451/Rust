## README for Plugin Info

BiTurbo plugin
All modular cars are now equiped with a turbo and are tuned to deliver more horsepower. Just hold right click to open the wastegate and prepare to see some flames in your wake!

TowCars plugin
I wanted to give some more utility to cars in rust. Sometimes I want to pull other cars around and this plugin will let you do that. 
First take out your hammer 
Next middle click on the car you are pulling with
Then middle click on the car you want to tow
To remove the hook, just middle click on one of the two cars.

## About BiTurbo
How to use:
Hold right click while driving to open the wastegate

To grant permission to a specific player:
```
oxide.grant user <player_name> BiTurbo.use
```
To grant permission to a group:
```
oxide.grant group <group_name> BiTurbo.use
```
To remove permission from a player:
```
oxide.revoke user <player_name> BiTurbo.use
```
To remove permission from a group:
```
oxide.revoke group <group_name> BiTurbo.use
```
The plugin will automatically check for permissions when the command is used and will enforce any configuration restrictions that are set.

### About TowCars
Commands:

/tow - The main command to toggle towing. When used:
If not towing: Attempts to tow a vehicle in front of you
If already towing: Releases the current tow connection
Permissions:

towcars.use - Required to use the /tow command
To set up the permissions on a Rust server:

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
