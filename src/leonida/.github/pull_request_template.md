Claimed in the coordination issue as: <!-- the item ID from the open "Coordinating who works on what" issue,
     for example TEST-17, or a link to your claim -->

Depends on: <!-- #N for every pull request this one builds on or conflicts with (the coordination issue's
     Commands section, AGENTS.md); delete the line if it builds on none and conflicts with none -->

Fixes: <!-- #N for each issue this closes (GitHub closes it on merge); delete the line if it closes none -->

### What and why

<!-- What this changes and the problem it solves. -->

### Tested

<!-- What you ran and on which GPU and driver (CONTRIBUTING.md). For the shader translator: how many shaders
     translate and pass spirv-val, before and after. -->

GPUs tested: <!-- vendor, model, driver and OS of each GPU the GPU tests ran on, or n/a -->

AI-assisted: <!-- yes or no (AGENTS.md asks a coding agent to say so) -->

### Checklist

- [ ] Based on current `main` and merges cleanly (a conflicting pull request is labeled `needs rebase`).
- [ ] One topic; follow-up work goes in a new pull request.
- [ ] No game data, keys or decryption (CONTRIBUTING.md), including in logs or reports quoted here.
- [ ] A new translator rule is in the contract table of `docs/design.md`, with how it was confirmed.
