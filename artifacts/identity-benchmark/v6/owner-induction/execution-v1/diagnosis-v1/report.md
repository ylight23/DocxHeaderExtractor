# V6C primary failure diagnosis

Offline raw-response forensic only. No provider, Gold, V5C, or V6A reads; no prediction or validator mutation.

- {
  "sequence": 1,
  "documentId": "DOC-0123",
  "rawResponseSha256": "b5847ed5ed1ef122bde5911829b5b35a74cfdf69664d66405a5391b5a9d0a3c3",
  "providerStatus": "INVALID_SCHEMA",
  "ownerCount": 49,
  "assignmentCount": 116,
  "unresolvedCount": 0,
  "badMemberCount": 0,
  "badEvidenceCount": 49,
  "duplicateMemberCount": 0,
  "duplicateAssignmentCount": 0,
  "missingCoverageCount": 0,
  "primaryFailure": "FABRICATED_EVIDENCE_REFERENCE",
  "examples": {
    "badMembers": [],
    "badEvidence": [
      "O-01:SC-c5711328ecc9f9cb",
      "O-02:SC-75b66683b75b0c9d",
      "O-03:SC-d9213bae8cb6cb04",
      "O-04:SC-e8f95e9d0abd1e12",
      "O-05:SC-7059833e26bf82f4"
    ],
    "duplicateMembers": [],
    "missingCoverage": []
  }
}
- {
  "sequence": 2,
  "documentId": "DOC-0133",
  "rawResponseSha256": "b8a0f27dc70de6bad7d93272b3b4d88a1ebbe8943d501b6cb138fbd3052dbd82",
  "providerStatus": "INVALID_SCHEMA",
  "ownerCount": 28,
  "assignmentCount": 57,
  "unresolvedCount": 0,
  "badMemberCount": 57,
  "badEvidenceCount": 28,
  "duplicateMemberCount": 0,
  "duplicateAssignmentCount": 0,
  "missingCoverageCount": 0,
  "primaryFailure": "UNKNOWN_OR_DUPLICATE_MEMBER",
  "examples": {
    "badMembers": [
      "DOC-0133:V4P0005-0935:B000005",
      "DOC-0133:V4P0005-0935:B000935",
      "DOC-0133:V4P0011-0013:B000011",
      "DOC-0133:V4P0011-0013:B000013",
      "DOC-0133:V4P0011-0013:B000034"
    ],
    "badEvidence": [
      "DOC-0133:V4P0005-0935:SC-948f7fb1a513c101",
      "DOC-0133:V4P0011-0013:SC-11d1b04f16c142d4",
      "DOC-0133:V4P0015-0616:SC-c483be6ba7146330",
      "DOC-0133:V4P0017-0921:SC-193e908c0f00a68c",
      "DOC-0133:P36-41:SC-054b6bb0ac5b9b45"
    ],
    "duplicateMembers": [],
    "missingCoverage": []
  }
}
- {
  "sequence": 3,
  "documentId": "DOC-0252",
  "rawResponseSha256": "15342fcd48920b6171e1324e193bc281fdb867622425d522af6e04dc920b780f",
  "providerStatus": "INVALID_SCHEMA",
  "ownerCount": 53,
  "assignmentCount": 53,
  "unresolvedCount": 0,
  "badMemberCount": 53,
  "badEvidenceCount": 53,
  "duplicateMemberCount": 0,
  "duplicateAssignmentCount": 0,
  "missingCoverageCount": 0,
  "primaryFailure": "UNKNOWN_OR_DUPLICATE_MEMBER",
  "examples": {
    "badMembers": [
      "O1:B000006",
      "O2:B000016",
      "O3:B000022",
      "O4:B000026",
      "O5:B000031"
    ],
    "badEvidence": [
      "O1:SHARED_STRUCTURAL_HEADING_KEY",
      "O2:NORMALIZED_TEXT_AFFINITY",
      "O3:NORMALIZED_TEXT_AFFINITY",
      "O4:NORMALIZED_TEXT_AFFINITY",
      "O5:NORMALIZED_TEXT_AFFINITY"
    ],
    "duplicateMembers": [],
    "missingCoverage": []
  }
}
