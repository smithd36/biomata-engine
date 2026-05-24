## Recommendation for Next Milestone

We are at the perfect point to focus on **action validation + observation contracts**.

Specifically:

1. **Validate agent-returned actions against registered schemas**

   * Reject invalid commands cleanly.

2. **Add typed parameter schemas**

   Example:

   ```python
   params = {
       "target_agent": str,
       "text": str
   }
   ```

   Or adopt a JSON Schema-style approach.

3. **Introduce an observation contract system**

   * Mirror the action architecture for consistency.

4. **Then return to the village showcase**

   * At that point, the showcase will be built on real platform abstractions, not demo hacks.
