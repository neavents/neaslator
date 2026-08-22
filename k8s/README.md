# Kubernetes manifests — neaslator

Generated from the catalogue in the estate root (`Neavents/k8s/services.json`) by
`Neavents/k8s/generate.py`. **Edit the catalogue and regenerate**; edits made here are
overwritten.

They are checked in rather than generated at deploy time because this is a polyrepo: a
manifest that only exists when someone runs a script in a sibling checkout is a manifest
that is missing exactly when the repo is cloned on its own.

## Applying

    kubectl apply -k k8s/

The namespace, the shared `neavents-platform` ConfigMap and the backing services
(PostgreSQL, Garnet, RabbitMQ) come from the estate root — see `Neavents/k8s/README.md`.
This directory deploys the service and nothing else, so applying it against a cluster
without those leaves the pods in CrashLoopBackOff on a missing connection string.

## Secrets

Connection strings come from a Secret named `neaslator-secrets`, referenced with
`optional: true` so the manifests apply against a cluster that has not been given
credentials yet — the pod then fails its readiness probe rather than failing to schedule,
which is the more diagnosable of the two.

## What is deliberately not here

`kubernetes/` in this repo (where present) holds production-topology templates — Linkerd
Server/AuthorizationPolicy, NetworkPolicy, HPA. Those describe a cluster this estate does
not run yet. This directory is what actually deploys.
