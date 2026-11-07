# Prediction Bot

Watches open Twitch tabs, records every channel-points prediction and its result, and works out
which kind of bet has paid best historically.

## Run it

1. Start the app: `./run.sh`
2. Load the hook into Firefox: open `about:debugging#/runtime/this-firefox` → **Load Temporary
   Add-on…** → pick `extension/manifest.json`. (Temporary add-ons are removed when Firefox closes,
   so repeat this after a restart.)
3. Open or reload a Twitch channel. The app's status line should turn purple: `● Hooked: <channel>`.

Data is stored in `~/.local/share/PredictionBot/predictions.db` (SQLite; tables `predictions` and
`outcomes`). Set `PREDICTIONBOT_DB` to use a different file. `./run.sh --headless` collects without
a window and prints the table to the terminal.

Every message the extension sends is recorded in `~/.local/share/PredictionBot/hook.log`, next to the
database. If the status line says a tab is not hooked, that file shows what stopped arriving and when.

## How it fits together

- `extension/hook.js` runs inside the Twitch page and listens in on what the page already receives
  (its realtime websocket and GraphQL responses). It is read-only.
- `extension/relay.js` → `background.js` forward that to the app at `127.0.0.1:47800`.
- `PredictionBot/Ingest.cs` parses it, `Db.cs` stores it, `Stats.cs` does the maths,
  `MainWindow.cs` draws it.

## Reading the numbers

Every choice in every resolved prediction counts as one bet you could have placed.

| Column | Meaning |
| --- | --- |
| W / N | Wins out of bets in this group |
| WIN % (95%) | Bayesian win rate with its 95% credible interval |
| CROWD | Average share of the pool on these choices, i.e. how often the crowd expected them to win |
| RETURN | What you'd have made per point by betting the same amount on every one of them |
| P(PROFIT) | Probability, given the history, that this group really has a positive return |

Odds groups are by share of the pool at close: low is under 30%, high is over 70%
(`LowBelow` / `HighAbove` in `Stats.cs`). A winning bet returns `pool ÷ points on that choice`.

The Bayesian prior assumes the crowd prices things fairly (zero expected return) and carries the
weight of two bets per payout band, so a group only looks profitable once the results say so.
"Best historical pick" is the group with the highest posterior-expected return among those with at
least 5 bets.

Things the numbers can't see: payouts are fixed when the prediction locks, so the ratio you bet at
is not the ratio you get paid at, and your own bet lowers your payout.
