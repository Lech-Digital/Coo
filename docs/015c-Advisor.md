# Increment 15c: Baby Advisor & Milestones

## Concept
A dedicated "Insights" or "Advisor" tab providing contextual, age-based information and a secure AI-powered Q&A interface for parents.

## Deliverables
1. **Milestone Feed (Static/Rule-based)** — *Done*
   - UI: A comprehensive expandable milestones & development guide card (`AdvisorPage.cs`).
   - Logic: Based on baby's age in days (`AdvisorKnowledgeBase.cs`), provides tailored physical, cognitive, social, and sleep guidance.
2. **Clickable Quick-Ask Pills** — *Done*
   - UI: Wrapping pill list with a shuffle button ("🎲 More questions").
   - Logic: Age-targeted curated questions with interactive tap-to-ask behavior.
3. **AI Chat Advisor (Gemini Integration)** — *Done*
   - UI: Multi-turn chat interface with chat bubble styling, instant query cancellation, and conversation history drawer.
   - Backend: `POST /api/advisor/ask` on ASP.NET Core API calling Gemini with secure system prompt and baby context.
4. **Rate Limiting & Premium** — *Done*
   - Context-aware responses with household entitlement check.
   - Distinct golden "coo Premium" branding across tabs.

## Technical Implementation
- Backend: Gemini SDK integration via `AdvisorEndpoints.cs` and `AdvisorService.cs`.
- Mobile Client: `AdvisorPage.cs`, `AdvisorViewModel.cs`, `AdvisorThreadStore.cs`, `AdvisorClient.cs`, `CooPageHeader.cs`.
- Storage: Local SQLite storage for multi-thread conversation history (`AdvisorThreadStore`).
