1. **Add `ModelsTests.cs` to test the internal model records**.
   - Use `write_file` to create `tests/LogicAppQuery.Tests/ModelsTests.cs` and test constructors and properties for `ResourceItem`, `WorkflowRunProperties`, etc.
   - Use `write_file` to replace the `ModelsTests.cs` with the full suite.

2. **Add `ArmClientAdditionalTests.cs` to increase `ArmClient` coverage**.
   - Use `write_file` to create `tests/LogicAppQuery.Tests/ArmClientAdditionalTests.cs`.
   - Add tests for `DiscoverResourceGroupAsync` that returns multiple matches where one has `workflowapp` in `Kind`.
   - Add tests for `DiscoverResourceGroupAsync` with transient empty pages to ensure the retry logic works correctly.

3. **Add `ProgramTests.cs` to improve coverage for `Program.cs`**.
   - Since testing `Program.cs` fully is difficult due to Azure calls, we will just cover invoking Main with empty and help arguments.
   - Using `write_file` to add `tests/LogicAppQuery.Tests/ProgramTests.cs`.

4. **Complete pre-commit steps to ensure proper testing, verification, review, and reflection are done**.
   - Use `run_in_bash_session` to run tests and pre-commit checks.

5. **Submit the Pull Request**.
   - Use `submit` to push the changes with the title formatted as '🧪 [description]' and the description containing '🎯 **What:**', '📊 **Coverage:**', and '✨ **Result:**' headers.
