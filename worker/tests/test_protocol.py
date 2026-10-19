import json
from uuid import UUID

import pytest

from detailframe_worker.protocol import (
    CancelRequest,
    CapabilitiesRequest,
    EnhanceRequest,
    ProtocolError,
    decode_message,
    encode_message,
)


REQUEST_ID = "00000000-0000-4000-8000-000000000001"
JOB_ID = "00000000-0000-4000-8000-000000000002"


def test_decodes_capabilities_request_with_uuid():
    message = decode_message(
        json.dumps({"protocol_version": 1, "type": "capabilities", "request_id": REQUEST_ID})
    )

    assert message == CapabilitiesRequest(UUID(REQUEST_ID))


def test_decodes_enhance_request_with_required_values():
    message = decode_message(
        json.dumps(
            {
                "protocol_version": 1,
                "type": "enhance",
                "job_id": JOB_ID,
                "model_id": "model-id",
                "scale": 4,
                "device": "cpu",
                "input_path": "C:\\images\\input.png",
                "output_path": "C:\\images\\output.png",
                "output_format": "png",
            }
        )
    )

    assert message == EnhanceRequest(
        UUID(JOB_ID),
        "model-id",
        4,
        "cpu",
        "C:\\images\\input.png",
        "C:\\images\\output.png",
        "png",
    )


def test_decodes_cancel_request():
    assert decode_message(
        json.dumps({"protocol_version": 1, "type": "cancel", "job_id": JOB_ID})
    ) == CancelRequest(UUID(JOB_ID))


def test_unsupported_version_preserves_valid_enhance_job_id():
    with pytest.raises(ProtocolError) as error:
        decode_message(
            json.dumps(
                {
                    "protocol_version": 2,
                    "type": "enhance",
                    "job_id": JOB_ID,
                }
            )
        )

    assert error.value.code == "UNSUPPORTED_VERSION"
    assert error.value.job_id == UUID(JOB_ID)


def test_unsupported_version_preserves_valid_cancel_job_id():
    with pytest.raises(ProtocolError) as error:
        decode_message(
            json.dumps(
                {
                    "protocol_version": 2,
                    "type": "cancel",
                    "job_id": JOB_ID,
                }
            )
        )

    assert error.value.code == "UNSUPPORTED_VERSION"
    assert error.value.job_id == UUID(JOB_ID)


def test_ignores_harmless_extension_fields():
    assert decode_message(
        json.dumps(
            {
                "protocol_version": 1,
                "type": "cancel",
                "job_id": JOB_ID,
                "trace_context": {"source": "controller"},
            }
        )
    ) == CancelRequest(UUID(JOB_ID))


@pytest.mark.parametrize(
    ("line", "code"),
    [
        ("{", "INVALID_JSON"),
        ('{"protocol_version":1,"type":"capabilities"}', "INVALID_REQUEST"),
        (
            json.dumps(
                {"protocol_version": 2, "type": "capabilities", "request_id": REQUEST_ID}
            ),
            "UNSUPPORTED_VERSION",
        ),
        (
            json.dumps({"protocol_version": 1, "type": "capabilities", "request_id": "bad"}),
            "INVALID_UUID",
        ),
        (
            json.dumps({"protocol_version": 1, "type": "unknown", "request_id": REQUEST_ID}),
            "UNKNOWN_TYPE",
        ),
    ],
)
def test_rejects_invalid_messages_with_structured_errors(line, code):
    with pytest.raises(ProtocolError) as error:
        decode_message(line)

    assert error.value.code == code


def test_rejects_enhance_with_non_absolute_input_path():
    line = json.dumps(
        {
            "protocol_version": 1,
            "type": "enhance",
            "job_id": JOB_ID,
            "model_id": "model-id",
            "scale": 4,
            "device": "cpu",
            "input_path": "input.png",
            "output_path": "C:\\images\\output.png",
            "output_format": "png",
        }
    )

    with pytest.raises(ProtocolError):
        decode_message(line)


def test_encodes_one_compact_json_object_per_line():
    encoded = encode_message(
        {"protocol_version": 1, "type": "cancelled", "job_id": UUID(JOB_ID)}
    )

    assert encoded == (
        '{"protocol_version":1,"type":"cancelled",'
        '"job_id":"00000000-0000-4000-8000-000000000002"}\n'
    )
    assert json.loads(encoded) == {
        "protocol_version": 1,
        "type": "cancelled",
        "job_id": JOB_ID,
    }
