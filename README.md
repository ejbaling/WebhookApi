## Testing the local Ollama endpoint via CLI

Use this curl + jq one-liner to stream from a local Ollama/Llama server and concatenate the incremental JSON pieces into a single JSON object:

```bash
curl -N -s -H "Content-Type: application/json" -d '{"model":"llama3.2:3b","messages":[{"role":"system","content":"You MUST reply only with JSON per the schema."},{"role":"user","content":"Extract the following fields from the message: name, email, phone, bookingId, airbnbId, amount, urgent.\nReturn ONLY valid JSON with keys: name, email, phone, bookingId, airbnbId, amount, urgent. Use null when missing for strings and false for `urgent`.\nMESSAGE:\nHi, I am Jane Doe. Email: jane@example.com. Phone: +1-555-1234. BookingId: ABC123. Amount: ₱2,483.65 PHP. Is this urgent?"}],"temperature":0.0,"max_tokens":300}' http://10.0.0.106:11434/api/chat | jq -s -r 'map((.message.content // .choices[0].delta.content // .choices[0].message.content // .choices[0].text) // "") | join("")'
```

Example output:

```json
{
  "name": "Jane Doe",
  "email": "jane@example.com",
  "phone": "+1-555-1234",
  "bookingId": "ABC123",
  "airbnbId": null,
  "amount": "₱2,483.65 PHP",
  "urgent": false
}
```

- `appsettings.Production.json` is the same as `appsettings.Development.json`
