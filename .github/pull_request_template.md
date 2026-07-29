## Summary

Describe the problem, the intended outcome, and the implementation at a reviewable level.

## Related issue or roadmap scope

Link the issue or identify the exact roadmap milestone/slice. State important behavior that deliberately remains out of scope.

## Type of change

- [ ] Bug fix
- [ ] Feature
- [ ] Refactor with no intended behavior change
- [ ] Tests
- [ ] Documentation
- [ ] Dependency or maintenance change

## Architecture

Identify the affected projects and explain why each change belongs there. Confirm that provider SDK and platform-specific types remain inside Infrastructure where required.

## Safety and privacy review

- [ ] Preview and explicit-confirmation behavior remains intact where applicable.
- [ ] No live save, backup-run, remote object, or credential can be silently deleted, moved, truncated, or overwritten.
- [ ] Path containment and create-only backup-content rules remain intact.
- [ ] Unavailable providers cannot silently fall back or become enabled prematurely.
- [ ] Cancellation, concurrency, and stale asynchronous results were considered.
- [ ] Logs, errors, tests, screenshots, and fixtures contain no credentials or personal data.
- [ ] The final diff contains no local database, downloaded credentials, tokens, private paths, or generated artifacts.

Explain any item that is not applicable:

## Automated verification

List exact commands and results, including test count, failures, skipped tests, build warnings, and build errors.

```text
dotnet restore Manager/Manager.sln
dotnet test Manager/Manager.sln
dotnet build Manager/Manager.sln --configuration Release
dotnet list Manager/Manager.sln package --vulnerable --include-transitive
git diff --check
```

## Manual verification

Describe manual scenarios completed with sanitized test data. Explicitly identify anything that remains pending or could not be tested.

## Documentation, persistence, and dependencies

- [ ] User-facing and developer documentation is updated.
- [ ] Roadmap checkboxes accurately represent completed and verified work.
- [ ] Schema, migration, and serialization effects are documented and tested.
- [ ] New or changed packages are justified and `THIRD-PARTY-NOTICES.md` is current.
- [ ] No documentation, persistence, or dependency change is required.

Select all applicable items and explain any unusual impact:

## Reviewer notes

Call out the highest-risk code, design tradeoffs, compatibility limitations, or areas where focused review would be valuable.

## Final checklist

- [ ] I read and followed `CONTRIBUTING.md` and `CODE_OF_CONDUCT.md`.
- [ ] This pull request has one clear purpose and contains no unrelated changes.
- [ ] Regression tests cover the changed behavior and important failure paths.
- [ ] The complete verification suite passes, or exact blockers are disclosed above.
- [ ] I have not claimed unperformed manual verification or incomplete roadmap work as complete.
