#!/bin/bash
rm ForesightArtifact.zip
zip -j ForesightArtifact.zip icon.png manifest.json ../README.md ../ForesightArtifact/bin/Release/netstandard2.0/ForesightArtifact.dll
