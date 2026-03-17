# DARCI — AWS Setup Guide

## Architecture

```
Your PC (DARCI runs here)
    │
    │  outbound HTTPS only — no open ports needed
    ▼
AWS (relay + storage)
 ├── SQS inbox queue   ← phone sends messages here
 ├── SQS outbox queue  ← DARCI posts responses here
 └── S3 bucket         ← DARCI uploads research files here
    │
    │  outbound HTTPS
    ▼
Your Phone (Android app)
```

**Your PC never needs to be reachable from the internet.**
DARCI only makes outbound connections to AWS. The phone talks to AWS, not to your PC.

---

## Step 1 — Create an AWS Account

If you don't have one: https://aws.amazon.com → Create account.

---

## Step 2 — Create Two SQS Queues

1. Open **SQS** in the AWS console → **Create queue**
2. Type: **Standard** (not FIFO — simpler and sufficient)
3. Name: `darci-inbox`
4. Leave all other settings default → Create
5. Repeat for `darci-outbox`
6. Copy the **Queue URL** for each — you'll need these later:
   ```
   https://sqs.us-west-1.amazonaws.com/865950114843/darci-inbox
   https://sqs.us-west-1.amazonaws.com/865950114843/darci-outbox
   ```

---

## Step 3 — Create an S3 Bucket

1. Open **S3** → **Create bucket**
2. Name: `darci-files-<your-name>` (must be globally unique)
3. Region: same as your SQS queues (e.g. `us-east-1`)
4. Block all public access: **ON** (files accessed only via pre-signed URLs)
5. Create bucket.

---

## Step 4 — Create an IAM User with Minimal Permissions

This user's credentials go into DARCI on your PC **and** the Android app.

1. Open **IAM** → **Users** → **Create user**
2. Name: `darci-relay`
3. **Permissions** → Attach policies directly → Create inline policy:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": [
        "sqs:SendMessage",
        "sqs:ReceiveMessage",
        "sqs:DeleteMessage",
        "sqs:GetQueueAttributes"
      ],
      "Resource": [
        "arn:aws:sqs:us-east-1:ACCOUNT_ID:darci-inbox",
        "arn:aws:sqs:us-east-1:ACCOUNT_ID:darci-outbox"
      ]
    },
    {
      "Effect": "Allow",
      "Action": [
        "s3:PutObject",
        "s3:GetObject",
        "s3:ListBucket",
        "s3:DeleteObject"
      ],
      "Resource": [
        "arn:aws:s3:::darci-files-<your-name>",
        "arn:aws:s3:::darci-files-<your-name>/*"
      ]
    }
  ]
}
```

Replace `ACCOUNT_ID` with your 12-digit AWS account ID and `darci-files-<your-name>` with your bucket name.

4. After the user is created → **Security credentials** tab → **Create access key**
5. Select: **Application running outside AWS**
6. Download the CSV — you get:
   - Access key ID
   - Secret access key

---

## Step 5 — Configure DARCI on Your PC

Add these to `DARCI-v4/Darci.Api/.env.local`:

```
DARCI_AWS_KEY_ID=AKIA...
DARCI_AWS_KEY_SECRET=your_secret
DARCI_AWS_REGION=us-east-1
DARCI_SQS_INBOX=https://sqs.us-east-1.amazonaws.com/123456789012/darci-inbox
DARCI_SQS_OUTBOX=https://sqs.us-east-1.amazonaws.com/123456789012/darci-outbox
DARCI_S3_BUCKET=darci-files-<your-name>
```

DARCI will automatically start the SQS relay on next launch.
Verify it's working: `GET http://localhost:5081/cloud/status`

---

## Step 6 — Configure the Android App

In `DARCI-App/local.properties`:

```
DARCI_AWS_ENABLED=true
DARCI_AWS_KEY_ID=AKIA...
DARCI_AWS_KEY_SECRET=your_secret
DARCI_AWS_REGION=us-east-1
DARCI_SQS_INBOX=https://sqs.us-east-1.amazonaws.com/123456789012/darci-inbox
DARCI_SQS_OUTBOX=https://sqs.us-east-1.amazonaws.com/123456789012/darci-outbox
DARCI_S3_BUCKET=darci-files-<your-name>
```

Build and install the APK. The badge in the top-right will show **AWS** when the relay is active.

> **Note:** `local.properties` is gitignored by default in Android projects.
> Never commit your AWS credentials to git.

---

## How It Works End-to-End

### Sending a message (phone → DARCI):
1. You type a message in the app
2. App sends JSON to SQS `darci-inbox` queue
3. DARCI's `SqsRelayService` polls inbox every ~2 s (10 s long-poll)
4. DARCI receives the message, processes it, generates a reply
5. DARCI posts reply to SQS `darci-outbox`
6. App polls outbox (10 s long-poll), receives reply, displays it

### Viewing a research file:
1. DARCI finishes a research task, writes a file to disk
2. DARCI calls `POST /cloud/upload` internally
3. File is uploaded to S3 under `sessions/{sessionId}/{filename}`
4. App's file list screen calls S3 to list files
5. Tap a file → generates a 1-hour pre-signed download URL → opens in a viewer app

---

## Costs (us-east-1, personal use)

| Service | Usage estimate | Monthly cost |
|---------|----------------|--------------|
| SQS     | ~50k msgs/month | **$0** (free tier: 1M/month) |
| S3      | 1 GB storage + downloads | **~$0.03** |
| **Total** | | **~$0/month** |

For typical personal use this will be free indefinitely.

---

## Security Notes

- The IAM policy above grants access only to the two queues and one bucket — nothing else
- Pre-signed S3 URLs expire after 1 hour
- Rotate credentials: IAM → User → Security credentials → Create new access key, delete old one
- For extra security, create separate IAM users for DARCI (read+write) and the app (write inbox, read outbox, read S3)
