game-ticker-restart-round = Restarting round...
game-ticker-start-round = The round is starting now...
game-ticker-start-round-cannot-start-game-mode-fallback = Failed to start {$failedGameMode} mode! Defaulting to {$fallbackMode}...
game-ticker-start-round-cannot-start-game-mode-restart = Failed to start {$failedGameMode} mode! Restarting round...
game-ticker-start-round-invalid-map = Selected map {$map} is inelligible for gamemode {$mode}. Gamemode may not function as intended...
game-ticker-unknown-role = Unknown
game-ticker-delay-start = Round start has been delayed for {$seconds} seconds.
game-ticker-pause-start = Round start has been paused.
game-ticker-pause-start-resumed = Round start countdown is now resumed.
game-ticker-player-join-game-message = Welcome to Space Station 14! If this is your first time playing, be sure to read the game rules, and don't be afraid to ask for help in LOOC (local OOC) or OOC (usually available only between rounds).
game-ticker-get-info-text = Hi and welcome to [color=white]Space Station 14![/color]
                            The current round is: [color=white]#{$roundId}[/color]
                            The current player count is: [color=white]{$playerCount}[/color]
                            The current map is: [color=white]{$mapName}[/color]
                            The current game mode is: [color=white]{$gmTitle}[/color]
                            >[color=yellow]{$desc}[/color]
game-ticker-get-info-preround-text = Hi and welcome to [color=white]Space Station 14![/color]
                            The current round is: [color=white]#{$roundId}[/color]
                            The current player count is: [color=white]{$playerCount}[/color] ([color=white]{$readyCount}[/color] {$readyCount ->
                                [one] is
                                *[other] are
                            } ready)
                            The current map is: [color=white]{$mapName}[/color]
                            The current game mode is: [color=white]{$gmTitle}[/color]
                            >[color=yellow]{$desc}[/color]
game-ticker-no-map-selected = [color=yellow]Map not yet selected![/color]
persistent-war-lobby-status = [color=white]War #{$warId} • Round #{$roundId}[/color]
    War {$status}
persistent-war-lobby-duration = War active: {$hours}h {$minutes}m
persistent-war-status-Active = active
persistent-war-status-Ended = ended
frontline-faction-one-name = Frontline Faction One
frontline-faction-one-description = The first frontline faction.
frontline-faction-two-name = Frontline Faction Two
frontline-faction-two-description = The second frontline faction.
frontline-territory-one-name = Old Pass
frontline-territory-one-description = A narrow route through the western hills.
frontline-territory-two-name = Iron Valley
frontline-territory-two-description = An industrial valley between the fronts.
frontline-territory-three-name = Crossroads
frontline-territory-three-description = Roads converge at the center of the war.
frontline-territory-four-name = Eastmarch
frontline-territory-four-description = The eastern approach to the front.
frontline-territory-five-name = South Ridge
frontline-territory-five-description = A defensible ridge overlooking the south.
frontline-faction-status = Faction: {$faction}
frontline-faction-unselected = unselected
frontline-faction-choose-button = Choose faction
frontline-faction-select-title = Choose faction
frontline-lobby-title = FRONTLINE 14
frontline-war-status = WAR #{$warId} — {$status}
frontline-war-active = ACTIVE
frontline-war-ended = ENDED
frontline-territory-status = Territories: {$owned} / {$total}
frontline-victory-status = Victory: {$required} points required
frontline-deploy-button = DEPLOY
frontline-deploy-unavailable = Deploy unavailable until faction is selected
frontline-victory-title = WAR #{$warId} ENDED
frontline-victory-faction = {$faction} Victory
frontline-victory-territories = Territories held: {$owned} / {$total}
frontline-victory-duration = War duration: {$days}d {$hours}h
frontline-victory-return-lobby = Return to Lobby
frontline-refinery-title = Frontline refinery
frontline-refinery-input-heading = Input chamber
frontline-refinery-input-empty = Empty. Use a valid resource stack on the refinery to insert it.
frontline-refinery-stack-line = {$name}: {$amount}
frontline-refinery-stack-amount = {$amount} {$name}
frontline-refinery-eject-all = Eject all input
frontline-refinery-recipes-heading = Recipes
frontline-refinery-recipe-line = {$input} → {$amount} {$output} ({$seconds} s)
frontline-refinery-queue-heading = Queue
frontline-refinery-queue-empty = No queued jobs.
frontline-refinery-status-processing = processing
frontline-refinery-status-waiting = waiting
frontline-refinery-recipe-steel = Basic materials refining
frontline-refinery-recipe-technology-alloy = Technology alloy synthesis
frontline-refinery-recipe-unknown = Unknown recipe
frontline-refinery-job-line = #{$position} {$recipe}: {$status}, {$seconds} s remaining
frontline-refinery-invalid-input = The refinery does not accept this item.
frontline-refinery-insufficient-input = The input chamber does not contain enough material for this recipe.

frontline-factory-title = Frontline factory
frontline-factory-input-heading = Input chamber
frontline-factory-input-empty = Empty. Use a valid resource stack on the factory to insert it.
frontline-factory-stack-line = {$name}: {$amount}
frontline-factory-stack-amount = {$amount} {$name}
frontline-factory-eject-all = Eject all input
frontline-factory-recipes-heading = Recipes
frontline-factory-recipe-line = {$recipe}: {$input} → {$amount} {$output} ({$seconds} s)
frontline-factory-produce = Produce: {$recipe}
frontline-factory-queue-heading = Queue
frontline-factory-queue-empty = No queued jobs.
frontline-factory-status-processing = processing
frontline-factory-status-waiting = waiting
frontline-factory-recipe-mk58 = Mk 58 pistol
frontline-factory-recipe-magazine-pistol = Pistol magazine
frontline-factory-recipe-brutepack = Bruise packs
frontline-factory-recipe-supply-crate = Frontline supply crate
frontline-factory-recipe-unknown = Unknown recipe
frontline-factory-item-unknown = Unknown item
frontline-factory-job-line = #{$position} {$recipe}: {$status}, {$seconds} s remaining
frontline-factory-invalid-input = The factory does not accept this item.
frontline-factory-insufficient-input = The input chamber does not contain enough material for this recipe.
frontline-respawn-choice-title = You died
frontline-respawn-choice-wait = Wait for revival. Your body can still be revived.
frontline-respawn-choice-respawn = Respawn
frontline-resource-field-examine = State: {$state}; reserve: {$reserve}; active nodes: {$active}; replenishment: {$seconds}s.
game-ticker-player-no-jobs-available-when-joining = When attempting to join to the game, no jobs were available.

# Displayed in chat to admins when a player joins
player-join-message = Player {$name} joined.
player-first-join-message = Player {$name} joined for the first time.

# Displayed in chat to admins when a player leaves
player-leave-message = Player {$name} left.

latejoin-arrival-announcement = {$character} ({$job}) has arrived at the station!
latejoin-arrival-announcement-special = {$job} {$character} on deck!
latejoin-arrival-sender = Station
latejoin-arrivals-direction = A shuttle transferring you to your station will arrive shortly.
latejoin-arrivals-direction-time = A shuttle transferring you to your station will arrive in {$time}.
latejoin-arrivals-dumped-from-shuttle = A mysterious force prevents you from leaving with the arrivals shuttle.
latejoin-arrivals-teleport-to-spawn = A mysterious force teleports you off the arrivals shuttle. Have a safe shift!

preset-not-enough-ready-players = Can't start {$presetName}. Requires {$minimumPlayers} players but we have {$readyPlayersCount}.
preset-no-one-ready = Can't start {$presetName}. No players are ready.

game-run-level-PreRoundLobby = Pre-round lobby
game-run-level-InRound = In round
game-run-level-PostRound = Post round
