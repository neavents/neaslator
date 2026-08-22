# Kubernetes manifests — neaslator

Generated from `Neavents/k8s/services.json` by `Neavents/k8s/generate.py`. **Edit the catalogue
and regenerate**; edits here are overwritten.

They are committed rather than generated at deploy time because this is a polyrepo: a manifest
that only exists when someone runs a script in a sibling checkout is missing exactly when the
repo is cloned on its own.

## Applying

These are the environment-independent pieces — no namespace, no image tag, no secrets, no
replica count that survives an overlay. On their own they are not deployable, and that is the
point: everything that differs between a laptop and production lives in
`Neavents/k8s/overlays/`.

    kubectl apply -k Neavents/k8s/overlays/local      # or overlays/prod

## Secrets

Configuration comes from a Secret named `neaslator-secrets`, referenced with `optional: true` so the
manifests apply against a cluster that has not been given credentials yet — the pod then fails
its readiness probe rather than failing to schedule, which is the more diagnosable of the two.
