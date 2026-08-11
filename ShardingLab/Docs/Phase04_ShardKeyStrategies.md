অবশ্যই। এটা মূলত **“আমাদের data-র জন্য কোন Shard Key সবচেয়ে ভালো?”** — সেটা experiment করে বের করছে।

সহজভাবে ধরো:

> **একটা chat app আছে → 4টা database/shard আছে → message কোন database-এ যাবে সেটা একটা key দিয়ে decide করতে হবে।**

এই Phase 4-এ ৩টা candidate key compare করা হচ্ছে:

* `MessageId`
* `UserId`
* `ConversationId`

শেষে দেখা হচ্ছে **কোন key ব্যবহার করলে একই conversation-এর messages একই shard-এ থাকে।**

---

# 1. প্রথমে পুরো idea

ধরো ৪টা database:

```text
Shard 0
Shard 1
Shard 2
Shard 3
```

একটা message এলো:

```text
MessageId = 101
ConversationId = 50
UserId = 10
```

কোন shard-এ যাবে?

সেটা করা হচ্ছে:

```text
Hash(key) % 4
```

যেমন:

```text
Hash(ConversationId 50) % 4
             ↓
            2
             ↓
        Shard 2
```

অর্থাৎ **যে key বেছে নেব, সেই key-এর hash অনুযায়ী data shard হবে।**

---

# 2. Phase 4 আসলে কী করছে?

আগের Phase 3-এ manually:

```text
ConversationId
      ↓
    hash
      ↓
Shard 0/1/2/3
```

করেছিলে।

এখন Phase 4 প্রশ্ন করছে:

> **ConversationId-ই কি সত্যিই best key?**

তাই ৩টা strategy test করছে:

```text
Hash(MessageId)
Hash(UserId)
Hash(ConversationId)
```

---

# 3. Constants গুলো কী?

```csharp
private const int ShardCount = 4;
```

মানে:

```text
আমাদের 4টা shard/database আছে
```

---

```csharp
private const int ConversationCount = 5_000;
```

মানে:

```text
5,000টা conversation
```

---

```csharp
private const int UserPoolSize = 2_000;
```

মানে:

```text
মোট 2,000 সম্ভাব্য user
```

---

```csharp
private const int MessagesPerConversation = 20;
```

মানে:

```text
প্রতিটি conversation-এ 20টা message
```

তাহলে মোট message:

```text
5,000 conversations × 20 messages
= 100,000 messages
```

---

# 4. SyntheticMessage কী?

```csharp
private record SyntheticMessage(
    long MessageId,
    long ConversationId,
    long UserId);
```

একটা message-এর জন্য শুধু ৩টা important information রাখা হচ্ছে:

```text
MessageId
ConversationId
UserId
```

যেমন:

```text
MessageId = 101
ConversationId = 50
UserId = 7
```

এটা **real database data না**।

এটা শুধু test করার জন্য fake/synthetic data।

---

# 5. Run() শুরু হলে কী হয়?

```csharp
var messages = GenerateSyntheticMessages();
```

প্রথমে fake messages বানানো হচ্ছে।

তারপর:

```text
5,000 conversations
×
20 messages
=
100,000 messages
```

generate হবে।

---

# 6. Fake data কীভাবে তৈরি হচ্ছে?

এই method:

```csharp
GenerateSyntheticMessages()
```

এর কাজ:

> **বাস্তব database ছাড়াই realistic-looking chat data তৈরি করা।**

---

## Random user তৈরি

```csharp
var random = new Random(7);
```

এখানে `7` গুরুত্বপূর্ণ।

কারণ fixed seed ব্যবহার করা হয়েছে।

তাই প্রতিবার program চালালে একই random data তৈরি হবে।

মানে:

```text
Run 1 → একই data
Run 2 → একই data
Run 3 → একই data
```

এতে experiment repeatable হয়।

---

# 7. Conversation তৈরি হচ্ছে

```csharp
for (long conversationId = 1;
     conversationId <= ConversationCount;
     conversationId++)
```

মানে:

```text
Conversation 1
Conversation 2
Conversation 3
...
Conversation 5000
```

---

# 8. প্রতিটি conversation-এর ২ জন user

```csharp
long userA = random.Next(1, UserPoolSize + 1);
long userB = random.Next(1, UserPoolSize + 1);
```

ধরো:

```text
Conversation 100

User A = 25
User B = 800
```

তাহলে conversation:

```text
User 25  ↔  User 800
```

---

# 9. প্রতিটি conversation-এ 20 messages

```csharp
for (int i = 0; i < MessagesPerConversation; i++)
```

মানে:

```text
Conversation 100

Message 1
Message 2
Message 3
...
Message 20
```

---

# 10. User A/B alternately message পাঠাচ্ছে

এই line:

```csharp
long userId = i % 2 == 0 ? userA : userB;
```

মানে:

```text
Message 1 → User A
Message 2 → User B
Message 3 → User A
Message 4 → User B
...
```

অর্থাৎ:

```text
Conversation 100

User A → message
User B → message
User A → message
User B → message
```

এটা realistic chat conversation-এর মতো।

---

# 11. এখন আসল experiment শুরু

এই ৩টা চালানো হচ্ছে:

```csharp
Evaluate("Hash(MessageId)", ...)
Evaluate("Hash(UserId)", ...)
Evaluate("Hash(ConversationId)", ...)
```

অর্থাৎ একই 100,000 messages-এর উপর ৩টা different shard key test হচ্ছে।

---

# 12. Evaluate() কী করছে?

প্রথমে:

```csharp
var shardSizes = new int[ShardCount];
```

এটা track করবে:

```text
Shard 0 → কত message?
Shard 1 → কত message?
Shard 2 → কত message?
Shard 3 → কত message?
```

---

আর:

```csharp
var shardsPerConversation =
    new Dictionary<long, HashSet<int>>();
```

এটা track করবে:

> **একটা conversation-এর messages কয়টা different shard-এ গেছে?**

এটাই সবচেয়ে important।

---

# 13. একটা example

ধরো Conversation 10-এর 20টা message।

### Case 1 — ভালো

```text
Conversation 10

Message 1  → Shard 2
Message 2  → Shard 2
Message 3  → Shard 2
...
Message 20 → Shard 2
```

তাহলে:

```text
Conversation 10 → only Shard 2
```

এটা excellent।

---

### Case 2 — খারাপ

```text
Conversation 10

Message 1  → Shard 0
Message 2  → Shard 3
Message 3  → Shard 1
Message 4  → Shard 0
...
```

তাহলে:

```text
Conversation 10
      ↓
Shard 0
Shard 1
Shard 2
Shard 3
```

এটাকে বলা হচ্ছে:

> **Split conversation**

---

# 14. Split conversation কেন খারাপ?

ধরো user খুললো:

```text
Conversation 10
```

তাকে দেখতে হবে:

```text
Message 1 → Shard 0
Message 2 → Shard 3
Message 3 → Shard 1
Message 4 → Shard 2
...
```

তাহলে application-কে:

```text
Shard 0 query
Shard 1 query
Shard 2 query
Shard 3 query
```

করতে হবে।

এটাকে বলা হয়:

> **Fan-out query**

এটা expensive এবং complicated।

---

# 15. Hash(MessageId) কেন খারাপ?

এখানে:

```csharp
Evaluate("Hash(MessageId)", messages, m => m.MessageId);
```

মানে:

```text
MessageId
   ↓
Hash
   ↓
Shard
```

প্রতিটি message-এর আলাদা MessageId:

```text
1
2
3
4
5
6
...
```

তাই একই conversation-এর message-গুলো randomভাবে বিভিন্ন shard-এ ছড়িয়ে যাবে।

যেমন:

```text
Conversation 100

Message 1 → Shard 0
Message 2 → Shard 2
Message 3 → Shard 1
Message 4 → Shard 3
...
```

### Result:

```text
একটা conversation
        ↓
অনেক shard
```

❌ Bad for chat history query.

---

# 16. Hash(UserId) কী করছে?

এবার:

```csharp
Evaluate("Hash(UserId)", messages, m => m.UserId);
```

মানে:

```text
UserId
  ↓
Hash
  ↓
Shard
```

ধরো:

```text
User A = 10 → Shard 1
User B = 20 → Shard 3
```

Conversation:

```text
User A ↔ User B
```

এখন:

```text
User A's messages → Shard 1
User B's messages → Shard 3
```

তাই একই conversation দুই shard-এ split হয়ে যাবে।

---

# 17. কিন্তু UserId কখন ভালো?

যদি application-এর প্রধান query হয়:

```text
"এই user-এর সব data দাও"
```

তাহলে:

```text
Hash(UserId)
```

খুব ভালো হতে পারে।

যেমন Instagram-এর মতো system-এ:

```text
Give me everything related to User 123
```

এটা dominant query হলে UserId sensible shard key।

---

# 18. Hash(ConversationId) কী করছে?

এখন সবচেয়ে important:

```csharp
Evaluate(
    "Hash(ConversationId)",
    messages,
    m => m.ConversationId);
```

মানে:

```text
ConversationId
      ↓
    Hash
      ↓
    Shard
```

ধরো:

```text
ConversationId = 100

Hash(100) % 4
      ↓
     2
```

তাহলে:

```text
Conversation 100
      ↓
   Shard 2
```

এখন ওই conversation-এর **সব message-এর ConversationId একই**:

```text
Message 1 → ConversationId 100
Message 2 → ConversationId 100
Message 3 → ConversationId 100
...
Message 20 → ConversationId 100
```

তাই:

```text
সব message
    ↓
Shard 2
```

🔥 এটাই desired behavior।

---

# 19. `StableHash` কেন?

এই line:

```csharp
int shard =
    (int)(StableHash.Of(keySelector(m)) % ShardCount);
```

এর কাজ:

```text
Key
 ↓
StableHash
 ↓
number
 ↓
% 4
 ↓
0 / 1 / 2 / 3
```

অর্থাৎ ৪টা shard-এর মধ্যে একটা নির্বাচন করা।

---

# 20. `% ShardCount` কেন?

কারণ:

```text
ShardCount = 4
```

তাই:

```text
Hash % 4
```

এর result হবে:

```text
0
1
2
3
```

এটাই আমাদের shard number।

---

# 21. `shardSizes` কেন?

প্রতিবার message route করার সময়:

```csharp
shardSizes[shard]++;
```

মানে count করছে:

```text
Shard 0 → 25,000 messages
Shard 1 → 25,100 messages
Shard 2 → 24,900 messages
Shard 3 → 25,000 messages
```

এটা দিয়ে বোঝা যায়:

> **Shard distribution balanced কিনা।**

---

# 22. `shardsPerConversation` কেন?

এই অংশ:

```csharp
if (!shardsPerConversation.TryGetValue(
        m.ConversationId,
        out var shardsSeen))
{
    shardsPerConversation[m.ConversationId] = shardsSeen = [];
}

shardsSeen.Add(shard);
```

ধরো:

```text
Conversation 10

Message 1 → Shard 2
Message 2 → Shard 2
Message 3 → Shard 2
```

তাহলে:

```text
Conversation 10 → {2}
```

একটা shard।

---

কিন্তু:

```text
Message 1 → Shard 2
Message 2 → Shard 1
Message 3 → Shard 2
```

তাহলে:

```text
Conversation 10 → {1, 2}
```

দুই shard।

তাই এটা split।

---

# 23. Split conversation count

```csharp
int splitConversations =
    shardsPerConversation.Values.Count(s => s.Count > 1);
```

মানে:

> কয়টা conversation একাধিক shard-এ ছড়িয়ে গেছে?

যদি:

```text
5,000 conversations
```

এর মধ্যে:

```text
4,900 split
```

তাহলে:

```text
4,900 / 5,000
= 98%
```

খারাপ।

---

# 24. `splitPct`

```csharp
double splitPct =
    100.0 * splitConversations
    / shardsPerConversation.Count;
```

মানে:

```text
কত % conversation split হয়েছে?
```

এটাই Phase 4-এর সবচেয়ে গুরুত্বপূর্ণ measurement।

---

# 25. Expected result কেমন হবে?

Conceptually:

```text
Hash(MessageId)
→ প্রায় সব conversation split

Hash(UserId)
→ অনেক conversation split

Hash(ConversationId)
→ 0% split
```

অর্থাৎ:

```text
                 Conversation split
                 
MessageId       ████████████████████
UserId          █████████████████
ConversationId  0
```

---

# 26. কেন ConversationId winner?

কারণ application-এর সবচেয়ে important query হলো:

```text
GetConversationMessages
```

User যখন একটা chat খুলে:

```text
GET /conversations/123/messages
```

তখন আমাদের দরকার:

```text
ConversationId = 123
```

এর সব messages।

তাই সবচেয়ে logical হলো:

```text
ConversationId
      ↓
Shard
```

এতে পুরো conversation এক জায়গায় থাকে।

---

# 27. একটা real-world picture

ধরো ৪টা shard:

```text
┌─────────┐
│ Shard 0 │
└─────────┘

┌─────────┐
│ Shard 1 │
└─────────┘

┌─────────┐
│ Shard 2 │
└─────────┘

┌─────────┐
│ Shard 3 │
└─────────┘
```

Conversation:

```text
Alice ↔ Bob
ConversationId = 500
```

Hash:

```text
Hash(500) % 4
       ↓
      2
```

তাই:

```text
             Conversation 500
                    │
                    ▼
               ┌─────────┐
               │ Shard 2 │
               └─────────┘
                    │
          ┌─────────┼─────────┐
          ▼         ▼         ▼
       Msg 1      Msg 2     Msg 3
       Msg 4      Msg 5     ...
```

🔥 পুরো conversation এক shard-এ।

---

# 28. "Who, What, Why, How, When"

### **What — কী হচ্ছে?**

৩টা shard-key strategy compare হচ্ছে:

```text
MessageId
UserId
ConversationId
```

---

### **Why — কেন?**

কারণ shard করার সময় সবচেয়ে important decision হলো:

> **কোন key দিয়ে data partition করব?**

ভুল key হলে query অনেক shard-এ যেতে হবে।

---

### **Who — কে decide করছে?**

Application code:

```csharp
Evaluate(...)
```

নিজেই decide করছে কোন key ব্যবহার করলে data কোন shard-এ যাবে।

Database নিজে এখানে decision নিচ্ছে না।

---

### **How — কীভাবে?**

```text
Shard Key
    ↓
StableHash
    ↓
% 4
    ↓
Shard 0/1/2/3
```

---

### **When — কখন এটা important?**

যখন database বড় হয়ে যায় এবং:

```text
একটা database আর যথেষ্ট না
```

তখন:

```text
Database
    ↓
Shard 0
Shard 1
Shard 2
Shard 3
```

করতে হয়।

তার আগে ঠিক করতে হয়:

> **কোন key দিয়ে data distribute করব?**

---

# 29. সবচেয়ে important concept

এখানে একটা খুব important System Design lesson আছে:

> **Best shard key is NOT necessarily the field that distributes data most evenly.**

বরং:

> **Best shard key হলো যেটা dominant query-কে একটি shard-এ রাখতে পারে এবং একই সাথে reasonably balanced distribution দেয়।**

এই example-এ:

```text
Dominant query:
GetConversationMessages
```

তাই:

```text
ConversationId
```

best।

---

# 30. Phase 3 বনাম Phase 4

### Phase 3

শিখেছিলে:

```text
ConversationId
      ↓
Hash
      ↓
Shard
```

অর্থাৎ **sharding কীভাবে implement করা যায়।**

### Phase 4

এখন শিখছো:

```text
MessageId      ──┐
UserId         ──┼──→ Which shard key is best?
ConversationId ──┘
```

অর্থাৎ:

> **কেন ConversationId বেছে নিলাম?**

এটাই Phase 4-এর মূল শিক্ষা।

---

## 🧠 End-of-the-day summary

এক লাইনে পুরো Phase 4:

> **Chat system-এ যদি সবচেয়ে common কাজ হয় "একটা conversation-এর সব messages পড়া", তাহলে ConversationId দিয়ে shard করা ভালো, কারণ একই conversation-এর সব messages একই shard-এ থাকে এবং query-কে multiple shard-এ fan-out করতে হয় না।**

আর তিনটা strategy মনে রাখো:

```text
MessageId
❌ Message ছড়িয়ে দেয়

UserId
⚠️ User-এর data একসাথে রাখে,
   কিন্তু conversation split হতে পারে

ConversationId
✅ পুরো conversation এক shard-এ রাখে
```

**এই Phase 4 আসলে code শেখানোর চেয়ে বেশি গুরুত্বপূর্ণ একটা System Design lesson শেখাচ্ছে: *"Shard key নির্বাচন করতে হলে আগে তোমার dominant query pattern বুঝতে হবে।"***
