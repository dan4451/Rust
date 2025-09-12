Commands:

/tow - The main command to toggle towing. When used:
If not towing: Attempts to tow a vehicle in front of you
If already towing: Releases the current tow connection
Permissions:

towcars.use - Required to use the /tow command
To set up the permissions on a Rust server:

To grant permission to a specific player:
oxide.grant user <player_name> towcars.use

To grant permission to a group:
oxide.grant group <group_name> towcars.use

To remove permission from a player:
oxide.revoke user <player_name> towcars.use

To remove permission from a group:
oxide.revoke group <group_name> towcars.use

Configuration options that affect the towing behavior:
{
  "DragMultiplier": 1.25,        // Multiplier for towing vehicle's drag
  "MaxDistance": 40.0,           // Maximum distance before tow breaks
  "Cooldown": 30.0,              // Seconds between tow attempts
  "JointBreakForce": 30000.0,    // Force required to break the tow
  "JointBreakTorque": 30000.0,   // Torque required to break the tow
  "RequireVehicleOwnership": true,// Whether players need to own/have access to the vehicle to tow it
  "EnableDebugLogging": false     // Enable detailed logging for troubleshooting
}

The plugin will automatically check for permissions when the command is used and will enforce any configuration restrictions that are set.
