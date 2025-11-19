az acr login -n binarydad
docker build -f .\Dockerfile . -t binarydad.azurecr.io/firewallsync:latest
docker push binarydad.azurecr.io/firewallsync:latest