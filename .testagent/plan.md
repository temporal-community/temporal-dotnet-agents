# Broad test-generation plan

The authoritative implementation sequence is
`/private/tmp/temporal-agents-recommended-sequencing-plan.VQ1ZRq/implementation-plan.md`.

Each acceptance item in `research.md` maps to the same-numbered commit section in that plan. Within
each commit:

1. Add the smallest red test that demonstrates the verified defect or freezes the proposed contract.
2. Implement the production change without touching unrelated files.
3. Update XML documentation, architecture/how-to documentation, samples, and PublicAPI manifests
   named by the commit.
4. Run the narrow unit project, then relevant integration/replay/package gates.
5. Re-read assertions against the production branch and perform pseudo-mutation review.
6. Stage explicit files only, review the staged diff, and create one local commit before advancing.

Final validation covers Release solution build, all unit and non-capture integration tests, every
replay disposition, sample builds, both packed assets, and diff/worktree review.
