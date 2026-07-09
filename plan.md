1. **Modify `src/LogicAppQuery/ArmClient.cs`:**
   - Add a `private static bool IsAllowedHost(string host)` method to check if the host is a known Azure management domain (e.g., `management.azure.com`) or an Azure Storage endpoint (e.g., `*.blob.core.windows.net`, `*.file.core.windows.net`).
   - In `FetchContentAsync`, parse the URI using `Uri.TryCreate`. If parsing fails or `IsAllowedHost(parsedUri.Host)` returns false, return `null` to prevent arbitrary HTTP requests.
2. **Modify `tests/LogicAppQuery.Tests/ArmClientTests.cs`:**
   - Rename the existing test `FetchContentAsync_ArbitraryDomain_DoesNotSendBearerToken` to `FetchContentAsync_ArbitraryDomain_ReturnsNullAndMakesNoRequests`.
   - Update its assertions to verify that no HTTP requests are sent to `attacker.com` and `null` is returned.
   - Add a new test, `FetchContentAsync_AllowedStorageDomain_MakesRequest`, to ensure valid storage domains (e.g., `myaccount.blob.core.windows.net`) are successfully fetched.
3. **Visually Verify File Changes:**
   - Use the `read_file` tool to inspect the updated `ArmClient.cs` and `ArmClientTests.cs` files, ensuring the changes align with the plan.
4. **Run Tests:**
   - Execute `dotnet test` to confirm that all existing and new tests pass, verifying the SSRF vulnerability is fixed without breaking existing functionality.
5. **Complete Pre-Commit Steps:**
   - Complete pre-commit steps to ensure proper testing, verification, review, and reflection are done.
6. **Submit PR:**
   - Title: `🔒 Fix SSRF vulnerability in FetchContentAsync`
   - Description containing:
     * 🎯 **What:** The SSRF vulnerability fixed in `FetchContentAsync`.
     * ⚠️ **Risk:** An attacker controlling the output/input link URI could force the tool to make arbitrary GET requests to internal or external endpoints.
     * 🛡️ **Solution:** Implemented domain whitelisting to strictly allow only Azure Management and Azure Storage domains.
