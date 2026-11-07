#!/usr/bin/env bash
# Starts the Prediction Bot window.
cd "$(dirname "$0")/PredictionBot" && exec "$HOME/.dotnet/dotnet" run -c Release -- "$@"
