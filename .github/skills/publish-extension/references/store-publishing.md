# Microsoft Store publication is paused

The Store identity publisher differs from the Azure Artifact Signing Public Trust publisher used for community WinGet. The manual **Build Store package (paused)** workflow may create unsigned diagnostic artifacts, but it contains no Partner Center publication step.

Do not submit Store packages, change Store identity values, or reuse the community-WinGet publisher for Store builds until an explicit identity-migration plan has been reviewed and approved. See `DEVELOPMENT.md` for the preserved Store identity.
