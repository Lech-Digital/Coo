# Increment 12: Billing & Entitlements

## Goals
1. **Google Play Billing Integration**: Allow users to subscribe to Premium via Google Play (In-App Purchases).
2. **Server Verification**: The Android app sends the purchase token to the backend for verification.
3. **RTDN (Real-Time Developer Notifications)**: Backend listens to Google Cloud Pub/Sub to react to renewals, cancellations, and grace periods immediately.
4. **Entitlements**: The backend stores the subscription and applies a "Premium" entitlement to the whole Household.

## Proposed Plan

### 1. Backend Data Model
Add a new table `subscriptions` (Entity: `SubscriptionRecord`):
- `OriginalTransactionId` (or Purchase Token): Primary Key
- `HouseholdId`: The household receiving the premium features
- `UserId`: The user who bought it
- `Status`: Active, GracePeriod, Expired, Canceled
- `ExpiryDate`: When the premium access cuts off
- `ProductId`: The subscription tier

### 2. Backend API Endpoints
- `POST /api/billing/verify-receipt`: App calls this after purchase. Backend uses Google.Apis.AndroidPublisher.v3 to verify the token, creates/updates the `SubscriptionRecord`, and returns the entitlement status.
- `POST /api/billing/rtdn`: The webhook for Google Pub/Sub. Receives base64-encoded messages, validates the sender, and updates the `SubscriptionRecord` (e.g., extends expiry on renewal, marks as canceled).
- `GET /api/billing/entitlements`: App fetches this to see if the current household is premium. (Could also be part of the `/health` or `Sync` process, but a dedicated endpoint is cleaner).

### 3. Mobile App Changes
- Install `Plugin.InAppBilling` (James Montemagno).
- Create `IBillingService` to wrap the plugin (connect to store, fetch prices, purchase, handle pending transactions).
- Create a Paywall UI (e.g. `PremiumPage.cs`) displaying the benefits and price.
- Sync entitlement status on app start/resume and enforce it on premium features.

Does this look like the right direction for Increment 12?
