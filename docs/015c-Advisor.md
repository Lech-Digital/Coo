# Increment 15c: Baby Advisor & Milestones

## Concept
A dedicated "Insights" or "Advisor" tab providing contextual, age-based information and a secure AI-powered Q&A interface for parents.

## Deliverables
1. **Milestone Feed (Static/Rule-based)**
   - UI: A timeline card at the top of the tab.
   - Logic: Based on the baby's exact age (weeks/months), display anticipated developmental milestones (e.g., "Week 3: Growth spurt expected," "Month 4: Sleep regression").
2. **Clickable Quick-Ask Pills**
   - UI: Horizontal scrolling list of buttons above the chat input.
   - Logic: Dynamically generated questions relevant to the baby's age (e.g., "Is 4 hours between feeds normal?").
3. **AI Chat Advisor (Gemini Integration)**
   - UI: Conversational chat interface in the bottom half.
   - Backend: `POST /api/advisor/ask` endpoint.
   - Security: The mobile app NEVER holds the Gemini API key. The backend securely manages the prompt, injecting the baby's context (name, age) and a strict system prompt ("You are an empathetic pediatric advisor. No medical diagnoses...").
4. **Rate Limiting (Free vs Premium)**
   - Free Users: Limited to a small number of questions per day (e.g., 3).
   - Premium Users: Unlimited/High-limit questions, and context-aware answers (injecting recent log data into the prompt).

## Technical Implementation
- Backend needs the Google Gemini SDK.
- Need a new PostgreSQL table or Redis cache for tracking daily question counts per user (for rate limiting).
- Mobile UI needs a new MAUI page (`AdvisorPage.cs`) and ViewModel (`AdvisorViewModel.cs`).
