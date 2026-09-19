game-ticker-restart-round = Перезапуск раунда...
game-ticker-start-round = Раунд начинается...
game-ticker-start-round-cannot-start-game-mode-fallback = Не удалось запустить режим { $failedGameMode }! Запускаем { $fallbackMode }...
game-ticker-start-round-cannot-start-game-mode-restart = Не удалось запустить режим { $failedGameMode }! Перезапуск раунда...
game-ticker-start-round-invalid-map = Выбранная карта { $map } не подходит для игрового режима { $mode }. Игровой режим может не функционировать как задумано...
game-ticker-unknown-role = Неизвестный
game-ticker-delay-start = Начало раунда было отложено на { $seconds } секунд.
game-ticker-pause-start = Начало раунда было приостановлено.
game-ticker-pause-start-resumed = Отсчёт начала раунда возобновлён.
game-ticker-player-join-game-message = Добро пожаловать на Космическую Станцию 14! Если вы играете впервые, обязательно нажмите ESC на клавиатуре и прочитайте правила игры, а также не бойтесь просить помощи в "Админ помощь".
game-ticker-get-info-text = Привет и добро пожаловать в [color=white]Space Station 14![/color]
                            Текущий раунд: [color=white]#{ $roundId }[/color]
                            Текущее количество игроков: [color=white]{ $playerCount }[/color]
                            Текущая карта: [color=white]{ $mapName }[/color]
                            Текущий режим игры: [color=white]{ $gmTitle }[/color]
                            >[color=yellow]{ $desc }[/color]
game-ticker-get-info-preround-text = Привет и добро пожаловать в [color=white]Space Station 14![/color]
    Текущий раунд: [color=white]#{ $roundId }[/color]
    Текущее количество игроков: [color=white]{ $playerCount }[/color] ([color=white]{ $readyCount }[/color] { $readyCount ->
    [one] готов
    *[other] готовы
})
    Текущая карта: [color=white]{ $mapName }[/color]
    Текущий режим игры: [color=white]{ $gmTitle }[/color]
    >[color=yellow]{ $desc }[/color]
game-ticker-no-map-selected = [color=red]Карта ещё не выбрана![/color]
persistent-war-lobby-status = [color=white]Война #{$warId} • Раунд #{$roundId}[/color]
    Война {$status}
persistent-war-lobby-duration = Война идёт: {$hours}ч {$minutes}м
persistent-war-status-Active = идёт
persistent-war-status-Ended = завершена
frontline-faction-one-name = Первая фронтовая фракция
frontline-faction-one-description = Первая фракция фронта.
frontline-faction-two-name = Вторая фронтовая фракция
frontline-faction-two-description = Вторая фракция фронта.
frontline-faction-status = Фракция: {$faction}
frontline-faction-unselected = не выбрана
frontline-faction-choose-button = Выбрать сторону
frontline-faction-select-title = Выберите сторону
frontline-lobby-title = FRONTLINE 14
frontline-war-status = ВОЙНА #{$warId} — {$status}
frontline-war-active = ИДЁТ
frontline-war-ended = ЗАВЕРШЕНА
frontline-territory-status = Территории: {$owned} / {$total}
frontline-victory-status = Победа: контроль {$required} / {$total}
frontline-deploy-button = ВЫСАДИТЬСЯ
frontline-deploy-unavailable = Высадка недоступна, пока не выбрана фракция
frontline-victory-title = ВОЙНА #{$warId} ЗАВЕРШЕНА
frontline-victory-faction = Победа фракции {$faction}
frontline-victory-territories = Удержано территорий: {$owned} / {$total}
frontline-victory-duration = Длительность войны: {$days}д {$hours}ч
frontline-victory-return-lobby = Вернуться в лобби
frontline-refinery-title = Фронтовой переработчик
frontline-refinery-input-heading = Приёмная камера
frontline-refinery-input-empty = Пусто. Используйте подходящую стопку ресурсов на переработчике, чтобы загрузить её.
frontline-refinery-stack-line = {$name}: {$amount}
frontline-refinery-stack-amount = {$amount} {$name}
frontline-refinery-eject-all = Выгрузить всё сырьё
frontline-refinery-recipes-heading = Рецепты
frontline-refinery-recipe-line = {$input} → {$amount} {$output} ({$seconds} с)
frontline-refinery-queue-heading = Очередь
frontline-refinery-queue-empty = Очередь пуста.
frontline-refinery-status-processing = перерабатывается
frontline-refinery-status-waiting = ожидает
frontline-refinery-job-line = #{$position} {$recipe}: {$status}, осталось {$seconds} с
frontline-refinery-invalid-input = Переработчик не принимает этот предмет.
frontline-refinery-insufficient-input = В приёмной камере недостаточно материала для этого рецепта.
frontline-respawn-choice-title = Вы погибли
frontline-respawn-choice-wait = Ждите воскрешения. Ваше тело ещё можно воскресить.
frontline-respawn-choice-respawn = Возродиться
frontline-resource-field-examine = Состояние: {$state}; резерв: {$reserve}; активные узлы: {$active}; пополнение через: {$seconds} с.
game-ticker-player-no-jobs-available-when-joining = При попытке присоединиться к игре ни одной роли не было доступно.

# Displayed in chat to admins when a player joins
player-join-message = Игрок { $name } зашёл!
player-first-join-message = Игрок { $name } зашёл на сервер впервые.

# Displayed in chat to admins when a player leaves
player-leave-message = Игрок { $name } вышел!

latejoin-arrival-announcement = { $character } ({ $job }) { GENDER($entity) ->
    [male] прибыл
    [female] прибыла
    [epicene] прибыли
    *[neuter] прибыло
} на станцию!
latejoin-arrival-announcement-special = { $job } { $character } на палубе!
latejoin-arrival-sender = Станции
latejoin-arrivals-direction = Вскоре прибудет шаттл, который доставит вас на станцию.
latejoin-arrivals-direction-time = Шаттл, который доставит вас на станцию, прибудет через { $time }.
latejoin-arrivals-dumped-from-shuttle = Таинственная сила не позволяет вам улететь на шаттле прибытия.
latejoin-arrivals-teleport-to-spawn = Таинственная сила телепортирует вас с шаттла прибытия. Удачной смены!

preset-not-enough-ready-players = Не удалось запустить пресет { $presetName }. Требуется { $minimumPlayers } игроков, но готовы только { $readyPlayersCount }.
preset-no-one-ready = Не удалось запустить режим { $presetName }. Нет готовых игроков.

game-run-level-PreRoundLobby = Предраундовое лобби
game-run-level-InRound = В раунде
game-run-level-PostRound = После раунда
